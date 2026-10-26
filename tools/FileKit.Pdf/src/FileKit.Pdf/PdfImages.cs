using System;
using System.Collections.Generic;
using System.Linq;

namespace FileKit.Pdf;

public sealed record PdfImageInfo(PdfReference Reference, int Width, int Height, int BitsPerComponent,
    string ColorSpace /* DeviceRGB|DeviceGray|DeviceCMYK|Indexed|ICCBased|Other */, string? Filter /* DCTDecode|FlateDecode|JPXDecode|... */,
    long EncodedLength, bool HasSoftMask, IReadOnlyList<int> PageIndices);

public static class PdfImages
{
    public static IReadOnlyList<PdfImageInfo> Enumerate(PdfDocument document, int maxImages = 10_000, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<PdfReference, PdfImageInfo>();
        var pageIndicesByImage = new Dictionary<PdfReference, HashSet<int>>();

        for (int pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.Pages[pageIndex];
            var resources = document.ResolveDictionary(page.Resources);
            if (resources is null) continue;

            var visitedPath = new HashSet<int>();
            WalkResources(document, resources, pageIndex, 0, result, pageIndicesByImage, visitedPath, maxImages, cancellationToken);
            if (result.Count >= maxImages) break;
        }

        return result.Values.ToList();
    }

    private static void WalkResources(PdfDocument document, PdfDictionary resources, int pageIndex, int depth,
        Dictionary<PdfReference, PdfImageInfo> result, Dictionary<PdfReference, HashSet<int>> pageIndicesByImage,
        HashSet<int> visitedPath, int maxImages, CancellationToken cancellationToken)
    {
        if (depth > 10 || result.Count >= maxImages) return;

        var xobjects = document.ResolveDictionary(resources["XObject"]);
        if (xobjects is null) return;

        foreach (var (key, value) in xobjects.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Count >= maxImages) return;

            var reference = value as PdfReference;
            if (reference is null) continue;

            var stream = document.GetObject(reference.ObjectNumber) as PdfStream;
            if (stream is null) continue;

            var subtype = stream.Dictionary.GetName("Subtype");
            if (subtype == "Image")
            {
                if (!pageIndicesByImage.TryGetValue(reference, out var indices))
                {
                    indices = new HashSet<int>();
                    pageIndicesByImage[reference] = indices;
                }

                bool isNewPage = indices.Add(pageIndex);
                if (!result.ContainsKey(reference))
                {
                    var info = CreateImageInfo(document, reference, stream, indices.ToList());
                    result[reference] = info;
                }
                else if (isNewPage)
                {
                    var info = result[reference];
                    result[reference] = info with { PageIndices = indices.OrderBy(x => x).ToList() };
                }
            }
            else if (subtype == "Form")
            {
                if (visitedPath.Add(reference.ObjectNumber))
                {
                    var formResources = document.ResolveDictionary(stream.Dictionary["Resources"]);
                    if (formResources is not null)
                    {
                        WalkResources(document, formResources, pageIndex, depth + 1, result, pageIndicesByImage, visitedPath, maxImages, cancellationToken);
                    }
                    visitedPath.Remove(reference.ObjectNumber);
                }
            }
        }
    }

    private static PdfImageInfo CreateImageInfo(PdfDocument document, PdfReference reference, PdfStream stream, List<int> pageIndices)
    {
        var dict = stream.Dictionary;
        int width = (document.Resolve(dict["Width"]) as PdfInteger)?.Value is long w ? (int)w : 0;
        int height = (document.Resolve(dict["Height"]) as PdfInteger)?.Value is long h ? (int)h : 0;
        int bpc = (document.Resolve(dict["BitsPerComponent"]) as PdfInteger)?.Value is long b ? (int)b : 8;

        var csObj = document.Resolve(dict["ColorSpace"]);
        string colorSpace = "Other";
        if (csObj is PdfName csName) colorSpace = csName.Value;
        else if (csObj is PdfArray csArray && csArray.Count > 0 && csArray[0] is PdfName arrName) colorSpace = arrName.Value;

        var filterObj = document.Resolve(dict["Filter"]);
        string? filter = null;
        if (filterObj is PdfName filterName) filter = filterName.Value;
        else if (filterObj is PdfArray filterArray && filterArray.Count > 0 && filterArray[0] is PdfName arrFilterName) filter = arrFilterName.Value;

        bool hasSoftMask = dict["SMask"] is not null;
        long length = stream.Data.Length;

        return new PdfImageInfo(reference, width, height, bpc, colorSpace, filter, length, hasSoftMask, pageIndices);
    }

    public static byte[] GetEncodedData(PdfDocument document, PdfImageInfo image)
    {
        var stream = document.GetObject(image.Reference.ObjectNumber) as PdfStream;
        if (stream is null) throw new PdfOperationException("Image stream not found.");
        return stream.Data;
    }

    public static byte[]? TryDecodeFlatePixels(PdfDocument document, PdfImageInfo image)
    {
        if (image.Filter != "FlateDecode") return null;
        if (image.BitsPerComponent != 8) return null;
        if (image.ColorSpace != "DeviceGray" && image.ColorSpace != "DeviceRGB") return null;

        var stream = document.GetObject(image.Reference.ObjectNumber) as PdfStream;
        if (stream is null) return null;

        try
        {
            byte[] data = stream.Data;
            byte[] decompressed = PdfFilters.Inflate(data, PdfReadLimits.Default.MaxDecodedStreamBytes); // max bytes bounded by default limits

            PdfDictionary? decodeParms = null;
            var filterObj = document.Resolve(stream.Dictionary["Filter"]);
            var parmsObj = document.Resolve(stream.Dictionary["DecodeParms"]);

            if (filterObj is PdfArray filterArray && parmsObj is PdfArray parmsArray)
            {
                for (int i = 0; i < filterArray.Count && i < parmsArray.Count; i++)
                {
                    if ((document.Resolve(filterArray[i]) as PdfName)?.Value == "FlateDecode")
                    {
                        decodeParms = document.ResolveDictionary(parmsArray[i]);
                        break;
                    }
                }
            }
            else
            {
                decodeParms = document.ResolveDictionary(parmsObj);
            }
            if (decodeParms is not null)
            {
                int predictor = (int)((document.Resolve(decodeParms["Predictor"]) as PdfInteger)?.Value ?? 1);
                if (predictor > 1)
                {
                    int colors = image.ColorSpace == "DeviceRGB" ? 3 : 1;
                    long columns = image.Width;
                    int bits = (int)((document.Resolve(decodeParms["BitsPerComponent"]) as PdfInteger)?.Value ?? 8);

                    if (predictor == 2)
                        return null; // TIFF predictor not supported

                    if (columns <= 0 || image.Height <= 0 || bits <= 0)
                        throw new PdfOperationException("Invalid image dimensions or components.");

                    long bytesPerPixel = (colors * bits + 7) / 8;
                    long rowLength = (colors * bits * columns + 7) / 8;

                    long maxBytes = PdfReadLimits.Default.MaxDecodedStreamBytes;
                    if (rowLength > maxBytes)
                        throw new PdfLimitExceededException("Image row width exceeds memory limits.");

                    long expectedDecompressed = (long)image.Height * (rowLength + 1);
                    if (expectedDecompressed > maxBytes)
                        throw new PdfLimitExceededException("Image dimensions exceed memory budget.");

                    if (decompressed.Length != expectedDecompressed)
                        throw new PdfOperationException("Image dimensions do not match compressed data size.");

                    return PdfFilters.PngUnfilter(decompressed, (int)rowLength, (int)bytesPerPixel);
                }
            }

            return decompressed;
        }
        catch (PdfException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}

public sealed record PdfImageReplacement(byte[] JpegData, int Width, int Height, int Components);
