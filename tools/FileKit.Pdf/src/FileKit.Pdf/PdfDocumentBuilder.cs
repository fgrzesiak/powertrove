using System.Security.Cryptography;
using System.Text;

namespace FileKit.Pdf;

public sealed record PdfWriteOptions
{
    /// <summary>Drops document information, XMP metadata and per-page metadata/private data.</summary>
    public bool RemoveMetadata { get; init; }

    /// <summary>Sanitization flags to remove hidden content.</summary>
    public PdfSanitizeFlags Sanitize { get; init; } = PdfSanitizeFlags.None;

    /// <summary>Value written to /Producer (omitted when null).</summary>
    public string? Producer { get; init; } = "FileKit.Pdf";

    /// <summary>Uses object streams, a cross-reference stream, stream compression and deduplication.</summary>
    public bool Compact { get; init; }

    /// <summary>When supplied, produce a password-protected AES-256 R6 document.</summary>
    public PdfEncryptionOptions? Encryption { get; init; }
    public bool FlattenAnnotations { get; init; }
    public bool KeepFlattenedLinks { get; init; } = true;

    /// <summary>For deterministic tests (overrides the random ID).</summary>
    public Func<byte[]>? IdGenerator { get; init; }

    public bool EditMetadata { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Subject { get; init; }
    public string? Keywords { get; init; }

    public static PdfWriteOptions Default { get; } = new();
}

/// <summary>
/// Builds a new PDF from pages of existing documents and image pages.
/// Each page's resources are copied with all referenced objects (shared objects
/// are copied once per source). References to pages that are not part of the new
/// document become null, so excluded pages are never pulled in through links.
/// Document-level structures (bookmarks, named destinations, interactive form
/// definitions, tagged structure) are not carried over.
/// </summary>
public sealed class PdfDocumentBuilder
{
    private readonly List<PageSpec> _pages = new();
    private readonly Dictionary<(Guid, int), PdfImageReplacement> _replacements = new();
    private IReadOnlyList<PdfOutlineItem>? _outline;

    private abstract record PageSpec;
    private sealed record SourcePage(PdfDocument Document, int PageIndex, int RotationDelta, IReadOnlyList<PdfDecoration>? Decorations = null) : PageSpec;
    private sealed record ImagePage(PdfImage Image, PdfImagePageLayout Layout) : PageSpec;
    private sealed record BlankPage(double Width, double Height) : PageSpec;
    private sealed record ComposedPage(double Width, double Height, IReadOnlyList<(PdfDocument Document, int PageIndex, (double a, double b, double c, double d, double e, double f) Matrix)> Layers) : PageSpec;

    public int PageCount => _pages.Count;

    public PdfDocumentBuilder SetOutline(IReadOnlyList<PdfOutlineItem> items)
    {
        _outline = items;
        return this;
    }

    public PdfDocumentBuilder AddPage(PdfDocument source, int pageIndex, int rotationDelta = 0, IReadOnlyList<PdfDecoration>? decorations = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (pageIndex < 0 || pageIndex >= source.PageCount)
            throw new PdfOperationException("The page number is outside the document.");
        if (rotationDelta % 90 != 0)
            throw new PdfOperationException("Rotation must be a multiple of 90 degrees.");
        _pages.Add(new SourcePage(source, pageIndex, rotationDelta, decorations));
        return this;
    }

    public PdfDocumentBuilder ReplaceImage(PdfDocument source, PdfReference image, PdfImageReplacement replacement)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(replacement);

        var stream = source.GetObject(image.ObjectNumber) as PdfStream;
        if (stream is null || stream.Dictionary.GetName("Subtype") != "Image")
            throw new PdfOperationException("The reference does not point to an image.");

        double oldWidth = (source.Resolve(stream.Dictionary["Width"]) as PdfInteger)?.Value ?? 0;
        double oldHeight = (source.Resolve(stream.Dictionary["Height"]) as PdfInteger)?.Value ?? 0;

        if (oldWidth <= 0 || oldHeight <= 0)
            throw new PdfOperationException("The original image has invalid dimensions.");

        double oldRatio = oldWidth / oldHeight;
        double newRatio = (double)replacement.Width / replacement.Height;

        if (Math.Abs(oldRatio - newRatio) > 0.01 * oldRatio)
            throw new PdfOperationException("The replacement image aspect ratio differs by more than 1%.");

        _replacements[(source.InstanceId, image.ObjectNumber)] = replacement;
        return this;
    }

    public PdfDocumentBuilder AddPages(PdfDocument source, IEnumerable<int> pageIndices, int rotationDelta = 0)
    {
        foreach (int index in pageIndices) AddPage(source, index, rotationDelta);
        return this;
    }

    public PdfDocumentBuilder AddImagePage(PdfImage image, PdfImagePageLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        _pages.Add(new ImagePage(image, layout ?? PdfImagePageLayout.Default));
        return this;
    }

    public PdfDocumentBuilder AddBlankPage(double widthPt, double heightPt)
    {
        if (widthPt <= 0 || heightPt <= 0)
            throw new PdfOperationException("Page dimensions must be positive.");
        _pages.Add(new BlankPage(widthPt, heightPt));
        return this;
    }

    public PdfDocumentBuilder AddComposedPage(double width, double height, IReadOnlyList<(PdfDocument document, int pageIndex, (double a, double b, double c, double d, double e, double f) matrix)> layers)
    {
        if (width <= 0 || height <= 0)
            throw new PdfOperationException("Page dimensions must be positive.");
        ArgumentNullException.ThrowIfNull(layers);
        _pages.Add(new ComposedPage(width, height, layers));
        return this;
    }

    public void Save(Stream output, PdfWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        options ??= PdfWriteOptions.Default;
        if (_pages.Count == 0)
            throw new PdfOperationException("The new document would have no pages.");

        var writer = new ObjectTable(options);
        int catalogNumber = writer.Allocate();
        int pagesNumber = writer.Allocate();
        var pagesRef = new PdfReference(pagesNumber, 0);

        // Phase 1: allocate page objects so links between included pages can be mapped.
        var pageNumbers = new int[_pages.Count];
        var firstInstance = new Dictionary<(Guid, int), int>();
        for (int i = 0; i < _pages.Count; i++)
        {
            pageNumbers[i] = writer.Allocate();
            if (_pages[i] is SourcePage sp)
            {
                firstInstance.TryAdd((sp.Document.InstanceId, sp.Document.Pages[sp.PageIndex].Reference.ObjectNumber), pageNumbers[i]);
            }
            else if (_pages[i] is ComposedPage cp)
            {
                foreach (var layer in cp.Layers)
                {
                    firstInstance.TryAdd((layer.Document.InstanceId, layer.Document.Pages[layer.PageIndex].Reference.ObjectNumber), pageNumbers[i]);
                }
            }
        }

        var imageCache = new Dictionary<PdfImage, int>();
        var copiers = new Dictionary<Guid, ObjectCopier>();
        ObjectCopier CopierFor(PdfDocument document)
        {
            if (!copiers.TryGetValue(document.InstanceId, out var copier))
            {
                copier = new ObjectCopier(document, writer, firstInstance, options, _replacements, imageCache);
                copiers[document.InstanceId] = copier;
            }
            return copier;
        }

        var formXObjectsCache = new Dictionary<(Guid, int), (int FormNum, int Rotate, PdfArray? BBox)>();

        // Phase 2: copy page dictionaries and everything they reference.
        var kids = new PdfArray();
        for (int i = 0; i < _pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageRef = new PdfReference(pageNumbers[i], 0);
            kids.Items.Add(pageRef);

            PdfDictionary page = _pages[i] switch
            {
                SourcePage sp => CopierFor(sp.Document).CopyPage(sp.Document.Pages[sp.PageIndex], sp.RotationDelta, pagesRef, sp.Decorations, writer, cancellationToken),
                ImagePage ip => BuildImagePage(ip, writer, pagesRef),
                BlankPage bp => BuildBlankPage(bp, pagesRef),
                ComposedPage cp => BuildComposedPage(cp, pagesRef, copiers, firstInstance, options, writer, cancellationToken, formXObjectsCache),
                _ => throw new InvalidOperationException()
            };
            writer.Set(pageNumbers[i], page);
        }

        foreach (var copier in copiers.Values) copier.Drain(cancellationToken);

        var pagesDict = new PdfDictionary
        {
            ["Type"] = new PdfName("Pages"),
            ["Kids"] = kids,
            ["Count"] = new PdfInteger(_pages.Count)
        };
        writer.Set(pagesNumber, pagesDict);

        var catalog = new PdfDictionary
        {
            ["Type"] = new PdfName("Catalog"),
            ["Pages"] = pagesRef
        };

        // Optional content definitions are needed to render layered documents correctly.
        var firstSource = _pages.OfType<SourcePage>().Select(p => p.Document).FirstOrDefault();
        if (firstSource is not null && firstSource.Catalog["OCProperties"] is PdfObject ocProperties)
        {
            var copier = CopierFor(firstSource);
            catalog["OCProperties"] = copier.CopyValue(ocProperties, 0);
            copier.Drain(cancellationToken);
        }

        if (_outline != null && _outline.Count > 0)
        {
            int outlinesNumber = writer.Allocate();
            var outlinesDict = new PdfDictionary { ["Type"] = new PdfName("Outlines") };

            var (firstObj, lastObj, totalCount) = WriteOutlineLevel(_outline, outlinesNumber, writer, pageNumbers);

            if (firstObj.HasValue)
            {
                outlinesDict["First"] = new PdfReference(firstObj.Value, 0);
                outlinesDict["Last"] = new PdfReference(lastObj!.Value, 0);
                outlinesDict["Count"] = new PdfInteger(totalCount);
                catalog["Outlines"] = new PdfReference(outlinesNumber, 0);
                writer.Set(outlinesNumber, outlinesDict);
            }
        }

        writer.Set(catalogNumber, catalog);

        PdfReference? infoRef = null;
        var info = BuildInfo(firstSource, options);
        if (info.Count > 0)
        {
            int infoNumber = writer.Allocate();
            writer.Set(infoNumber, info);
            infoRef = new PdfReference(infoNumber, 0);
        }

        writer.Write(output, new PdfReference(catalogNumber, 0), infoRef, cancellationToken);
    }

    private static PdfDictionary BuildInfo(PdfDocument? source, PdfWriteOptions options)
    {
        var info = new PdfDictionary();
        bool removeMetadata = options.RemoveMetadata || options.Sanitize.HasFlag(PdfSanitizeFlags.Metadata);

        if (options.EditMetadata)
        {
            if (!string.IsNullOrEmpty(options.Title)) info["Title"] = PdfString.FromText(options.Title);
            if (!string.IsNullOrEmpty(options.Author)) info["Author"] = PdfString.FromText(options.Author);
            if (!string.IsNullOrEmpty(options.Subject)) info["Subject"] = PdfString.FromText(options.Subject);
            if (!string.IsNullOrEmpty(options.Keywords)) info["Keywords"] = PdfString.FromText(options.Keywords);

            if (source?.Info is PdfDictionary sourceInfo)
            {
                if (source.Resolve(sourceInfo["Creator"]) is PdfString creatorValue)
                    info["Creator"] = creatorValue;
            }
        }
        else if (!removeMetadata && source?.Info is PdfDictionary sourceInfo)
        {
            foreach (var key in new[] { "Title", "Author", "Subject", "Keywords", "Creator" })
            {
                if (source.Resolve(sourceInfo[key]) is PdfString value)
                    info[key] = value;
            }
        }

        if (options.Producer is string producer)
            info["Producer"] = PdfString.FromText(producer);

        return info;
    }

    private static PdfDictionary BuildImagePage(ImagePage spec, ObjectTable writer, PdfReference parent)
    {
        var (width, height, matrix) = spec.Layout.Compute(spec.Image);

        int imageNumber = writer.Allocate();
        writer.Set(imageNumber, spec.Image.CreateXObject(writer));

        string content = $"q {string.Join(' ', matrix.Select(PdfSerializer.FormatReal))} cm /Im0 Do Q";
        int contentNumber = writer.Allocate();
        writer.Set(contentNumber, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(content)));

        return new PdfDictionary
        {
            ["Type"] = new PdfName("Page"),
            ["Parent"] = parent,
            ["MediaBox"] = new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfReal(width), new PdfReal(height) }),
            ["Resources"] = new PdfDictionary
            {
                ["XObject"] = new PdfDictionary { ["Im0"] = new PdfReference(imageNumber, 0) }
            },
            ["Contents"] = new PdfReference(contentNumber, 0)
        };
    }

    private static PdfDictionary BuildBlankPage(BlankPage spec, PdfReference parent)
    {
        return new PdfDictionary
        {
            ["Type"] = new PdfName("Page"),
            ["Parent"] = parent,
            ["MediaBox"] = new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfReal(spec.Width), new PdfReal(spec.Height) }),
            ["Resources"] = new PdfDictionary()
        };
    }
    private PdfDictionary BuildComposedPage(
        ComposedPage cp,
        PdfReference pagesRef,
        Dictionary<Guid, ObjectCopier> copiers,
        Dictionary<(Guid, int), int> firstInstance,
        PdfWriteOptions options,
        ObjectTable writer,
        CancellationToken cancellationToken,
        Dictionary<(Guid, int), (int FormNum, int Rotate, PdfArray? BBox)> formXObjectsCache)
    {
        var resources = new PdfDictionary();
        var xobjects = new PdfDictionary();
        resources["XObject"] = xobjects;

        var annots = new List<PdfObject>();
        var contentStream = new StringBuilder();

        int layerIndex = 0;
        foreach (var layer in cp.Layers)
        {
            string xobjName = $"Fm{layerIndex++}";
            if (!copiers.TryGetValue(layer.Document.InstanceId, out var copier))
            {
                copier = new ObjectCopier(layer.Document, writer, firstInstance, options, _replacements);
                copiers[layer.Document.InstanceId] = copier;
            }

            var layerPage = layer.Document.Pages[layer.PageIndex];
            var cacheKey = (layer.Document.InstanceId, layerPage.Reference.ObjectNumber);

            int rotate;
            PdfArray? bbox;

            if (formXObjectsCache.TryGetValue(cacheKey, out var cacheEntry))
            {
                xobjects[xobjName] = new PdfReference(cacheEntry.FormNum, 0);
                rotate = cacheEntry.Rotate;
                bbox = cacheEntry.BBox;
            }
            else
            {
                var formDict = new PdfDictionary
                {
                    ["Type"] = new PdfName("XObject"),
                    ["Subtype"] = new PdfName("Form"),
                    ["FormType"] = new PdfInteger(1),
                    ["BBox"] = copier.CopyValue(layerPage.CropBox ?? layerPage.MediaBox ?? new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfInteger(612), new PdfInteger(792) }), 0),
                };

                if (layerPage.Resources is not null)
                {
                    formDict["Resources"] = copier.CopyValue(layerPage.Resources, 0);
                }

                rotate = PdfDocument.NormalizeRotation(layerPage.Rotate);
                bbox = formDict["BBox"] as PdfArray;

                if (rotate != 0)
                {
                    if (bbox != null)
                    {
                        double[] v = bbox.Items.Select(item => layer.Document.Resolve(item) switch
                        {
                            PdfInteger n => n.Value,
                            PdfReal r => r.Value,
                            _ => 0d
                        }).ToArray();

                        double minX = v[0], minY = v[1], maxX = v[2], maxY = v[3];
                        var w = maxX - minX;
                        var h = maxY - minY;

                        double[] m = rotate switch {
                            90 => [0, 1, -1, 0, h, 0],
                            180 => [-1, 0, 0, -1, w, h],
                            270 => [0, -1, 1, 0, 0, w],
                            _ => [1, 0, 0, 1, 0, 0]
                        };

                        formDict["Matrix"] = new PdfArray(m.Select(x => new PdfReal(x)));
                    }
                }

                var contents = layerPage.Dictionary["Contents"];
                byte[] data = Array.Empty<byte>();
                if (contents is not null)
                {
                    var resolvedContents = layer.Document.Resolve(contents);
                    if (resolvedContents is PdfStream s)
                    {
                        data = s.Data;
                        // Preserve filters and decode params
                        if (s.Dictionary["Filter"] is PdfObject flt) formDict["Filter"] = copier.CopyValue(flt, 0);
                        if (s.Dictionary["DecodeParms"] is PdfObject dp) formDict["DecodeParms"] = copier.CopyValue(dp, 0);
                    }
                    else if (resolvedContents is PdfArray arr)
                    {
                        using var ms = new MemoryStream();
                        foreach (var item in arr.Items)
                        {
                            if (layer.Document.Resolve(item) is PdfStream st)
                            {
                                var decoded = PdfFilters.Decode(st, layer.Document.Resolve, 100_000_000);
                                ms.Write(decoded);
                                ms.WriteByte(10);
                            }
                        }
                        data = ms.ToArray();
                        // Because we decoded the array of streams, we DO NOT copy Filter.
                    }
                }

                int formNum = writer.Allocate();
                writer.Set(formNum, new PdfStream(formDict, data));
                formXObjectsCache[cacheKey] = (formNum, rotate, bbox);
                xobjects[xobjName] = new PdfReference(formNum, 0);
            }

            contentStream.Append("q\n");
            var mat = layer.Matrix;
            contentStream.Append($"{Format(mat.a)} {Format(mat.b)} {Format(mat.c)} {Format(mat.d)} {Format(mat.e)} {Format(mat.f)} cm\n");
            contentStream.Append($"/{xobjName} Do\n");
            contentStream.Append("Q\n");

            if (layerPage.Dictionary["Annots"] is not null)
            {
                var resolvedAnnots = layer.Document.Resolve(layerPage.Dictionary["Annots"]) as PdfArray;
                if (resolvedAnnots is not null)
                {
                    foreach (var annotRef in resolvedAnnots.Items)
                    {
                        var copiedAnnot = copier.CopyValue(annotRef, 0);
                        if (copiedAnnot is PdfReference r)
                        {
                            var annotDict = layer.Document.Resolve(annotRef) as PdfDictionary;
                            if (annotDict != null && annotDict["Rect"] is PdfArray rectArray)
                            {
                                double[] rv = rectArray.Items.Select(item => layer.Document.Resolve(item) switch
                                {
                                    PdfInteger n => n.Value,
                                    PdfReal d => d.Value,
                                    _ => 0d
                                }).ToArray();

                                if (rv.Length == 4)
                                {
                                    double rx1 = rv[0], ry1 = rv[1], rx2 = rv[2], ry2 = rv[3];

                                    double ma = 1, mb = 0, mc = 0, md = 1, me = 0, mf = 0;
                                    if (rotate != 0) {
                                        double bw = 0, bh = 0;
                                        if (bbox != null) {
                                            double[] v = bbox.Items.Select(item => layer.Document.Resolve(item) switch
                                            {
                                                PdfInteger n => n.Value,
                                                PdfReal d => d.Value,
                                                _ => 0d
                                            }).ToArray();
                                            bw = v[2] - v[0];
                                            bh = v[3] - v[1];
                                        }
                                        double[] mrot = rotate switch {
                                            90 => [0, 1, -1, 0, bh, 0],
                                            180 => [-1, 0, 0, -1, bw, bh],
                                            270 => [0, -1, 1, 0, 0, bw],
                                            _ => [1, 0, 0, 1, 0, 0]
                                        };
                                        ma = mrot[0]; mb = mrot[1]; mc = mrot[2]; md = mrot[3]; me = mrot[4]; mf = mrot[5];
                                    }

                                    double c_a = mat.a * ma + mat.c * mb;
                                    double c_b = mat.b * ma + mat.d * mb;
                                    double c_c = mat.a * mc + mat.c * md;
                                    double c_d = mat.b * mc + mat.d * md;
                                    double c_e = mat.a * me + mat.c * mf + mat.e;
                                    double c_f = mat.b * me + mat.d * mf + mat.f;

                                    double px1 = c_a * rx1 + c_c * ry1 + c_e;
                                    double py1 = c_b * rx1 + c_d * ry1 + c_f;
                                    double px2 = c_a * rx2 + c_c * ry2 + c_e;
                                    double py2 = c_b * rx2 + c_d * ry2 + c_f;
                                    double px3 = c_a * rx1 + c_c * ry2 + c_e;
                                    double py3 = c_b * rx1 + c_d * ry2 + c_f;
                                    double px4 = c_a * rx2 + c_c * ry1 + c_e;
                                    double py4 = c_b * rx2 + c_d * ry1 + c_f;

                                    double nMinX = Math.Min(Math.Min(px1, px2), Math.Min(px3, px4));
                                    double nMaxX = Math.Max(Math.Max(px1, px2), Math.Max(px3, px4));
                                    double nMinY = Math.Min(Math.Min(py1, py2), Math.Min(py3, py4));
                                    double nMaxY = Math.Max(Math.Max(py1, py2), Math.Max(py3, py4));

                                    if (writer.Get(r.ObjectNumber) is PdfDictionary dictToUpdate)
                                    {
                                        dictToUpdate["Rect"] = new PdfArray(new PdfObject[] { new PdfReal(nMinX), new PdfReal(nMinY), new PdfReal(nMaxX), new PdfReal(nMaxY) });
                                    }
                                }
                            }
                            annots.Add(copiedAnnot);
                        }
                    }
                }
            }
        }

        int contentNum = writer.Allocate();
        writer.Set(contentNum, new PdfStream(new PdfDictionary(), System.Text.Encoding.ASCII.GetBytes(contentStream.ToString())));

        var dict = new PdfDictionary
        {
            ["Type"] = new PdfName("Page"),
            ["Parent"] = pagesRef,
            ["MediaBox"] = new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfReal(cp.Width), new PdfReal(cp.Height) }),
            ["Resources"] = resources,
            ["Contents"] = new PdfReference(contentNum, 0)
        };

        if (annots.Count > 0)
        {
            dict["Annots"] = new PdfArray(annots);
        }

        return dict;
    }

    private static string Format(double value) => Math.Abs(value) < 1e-10 ? "0" : value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);


    /// <summary>Output object table with sequential numbering and serialization.</summary>


    private static (int? first, int? last, int count) WriteOutlineLevel(
        IReadOnlyList<PdfOutlineItem> items,
        int parentNum,
        ObjectTable writer,
        int[] pageNumbers)
    {
        if (items.Count == 0) return (null, null, 0);

        int[] itemNums = new int[items.Count];
        for (int i = 0; i < items.Count; i++) itemNums[i] = writer.Allocate();

        int totalDescendants = 0;

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var dict = new PdfDictionary();

            // Title
            string title = item.Title ?? string.Empty;
            if (title.Any(c => c > 127))
            {
                byte[] bytes = new byte[2 + title.Length * 2];
                bytes[0] = 0xFE;
                bytes[1] = 0xFF;
                Encoding.BigEndianUnicode.GetBytes(title, 0, title.Length, bytes, 2);
                dict["Title"] = new PdfString(bytes, true);
            }
            else
            {
                dict["Title"] = PdfString.FromText(title);
            }

            // Parent
            dict["Parent"] = new PdfReference(parentNum, 0);

            // Prev / Next
            if (i > 0) dict["Prev"] = new PdfReference(itemNums[i - 1], 0);
            if (i < items.Count - 1) dict["Next"] = new PdfReference(itemNums[i + 1], 0);

            // Dest
            if (item.PageIndex.HasValue && item.PageIndex.Value >= 0 && item.PageIndex.Value < pageNumbers.Length)
            {
                dict["Dest"] = new PdfArray(new PdfObject[] { new PdfReference(pageNumbers[item.PageIndex.Value], 0), new PdfName("Fit") });
            }

            // Children
            int myDescendants = 0;
            if (item.Children != null && item.Children.Count > 0)
            {
                var (childFirst, childLast, childCount) = WriteOutlineLevel(item.Children, itemNums[i], writer, pageNumbers);
                if (childFirst.HasValue)
                {
                    dict["First"] = new PdfReference(childFirst.Value, 0);
                    dict["Last"] = new PdfReference(childLast!.Value, 0);
                    dict["Count"] = new PdfInteger(childCount);
                    myDescendants = childCount;
                }
            }

            totalDescendants += 1 + myDescendants;
            writer.Set(itemNums[i], dict);
        }

        return (itemNums[0], itemNums[^1], totalDescendants);
    }
    internal sealed class ObjectTable
    {
        private readonly List<PdfObject?> _objects = new() { null }; // object 0 is the free list head
        private readonly PdfWriteOptions _options;
        private readonly HashSet<int> _deduplicationCandidates = new();
        private readonly HashSet<int> _removedObjects = new();

        public ObjectTable(PdfWriteOptions options)
        {
            _options = options;
        }

        public int Allocate()
        {
            _objects.Add(null);
            return _objects.Count - 1;
        }

        public void Set(int number, PdfObject value, bool deduplicate = false)
        {
            _objects[number] = value;
            // Page identity and page-tree topology must survive even when dictionaries match.
            if (_options.Compact && deduplicate && (value is PdfStream or PdfDictionary) &&
                (value as PdfDictionary)?.GetName("Type") is not ("Page" or "Pages" or "Catalog"))
                _deduplicationCandidates.Add(number);
        }
        public PdfObject? Get(int number) => _objects[number];

        public void Write(Stream output, PdfReference root, PdfReference? info, CancellationToken cancellationToken)
        {
            // AES-256 R6 uses standard indirect objects. Object streams would require
            // a separate encryption-aware compact writer; never output a partially encrypted PDF.
            if (_options.Encryption is not null && _options.Compact)
                throw new PdfOperationException("Compact encryption is not supported.");
            var counting = new CountingStream(output);
            PdfEncryptionWriter? encryptor = null;
            PdfReference? encryptRef = null;
            try
            {
                if (_options.Encryption is { } encryption)
                {
                    encryptor = new PdfEncryptionWriter(encryption);
                    int encryptNumber = Allocate();
                    encryptRef = new PdfReference(encryptNumber, 0);
                    Set(encryptNumber, encryptor.EncryptionDictionary);
                    for (int i = 1; i < encryptNumber; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (_objects[i] is { } item)
                            _objects[i] = encryptor.EncryptObject(item);
                    }
                }

            if (_options.Compact)
            {
                var remap = Deduplicate(cancellationToken);
                if (remap.TryGetValue(root.ObjectNumber, out int rootNumber)) root = new PdfReference(rootNumber, 0);
                if (info is not null && remap.TryGetValue(info.ObjectNumber, out int infoNumber)) info = new PdfReference(infoNumber, 0);
                WriteCompact(counting, root, info, cancellationToken);
            }
            else
            {
                WriteClassic(counting, root, info, encryptRef, encryptor?.DocumentId, cancellationToken);
            }
            }
            finally { encryptor?.Dispose(); }
        }

        private Dictionary<int, int> Deduplicate(CancellationToken cancellationToken)
        {
            // Only candidate numbers can change. Other objects are stable anchors, even
            // when they refer back to candidates (for example, the page tree).
            var dependencies = new Dictionary<int, int>();
            var dependents = new Dictionary<int, List<int>>();
            var ready = new PriorityQueue<int, int>();
            foreach (int number in _deduplicationCandidates.Order())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var children = new HashSet<int>();
                VisitReferences(_objects[number]!, r =>
                {
                    if (_deduplicationCandidates.Contains(r.ObjectNumber)) children.Add(r.ObjectNumber);
                    return r;
                }, cancellationToken);
                dependencies[number] = children.Count;
                foreach (int child in children)
                {
                    if (!dependents.TryGetValue(child, out var parents))
                        dependents[child] = parents = new List<int>();
                    parents.Add(number);
                }
                if (children.Count == 0) ready.Enqueue(number, number);
            }

            var remap = new Dictionary<int, int>();
            var hashes = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            PdfReference Remap(PdfReference reference) => remap.TryGetValue(reference.ObjectNumber, out int canonical)
                ? new PdfReference(canonical, 0) : reference;

            while (ready.TryDequeue(out int number, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Children are final now: equivalent parents can merge in this same pass.
                var value = VisitReferences(_objects[number]!, Remap, cancellationToken);
                byte[] bytes = Serialize(value);
                string hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (!hashes.TryGetValue(hash, out var matches))
                    hashes[hash] = matches = new List<int>();
                // Hashes only locate candidates; compare bytes before ever merging objects.
                int existing = matches.FirstOrDefault(n => bytes.AsSpan().SequenceEqual(Serialize(_objects[n]!)));
                if (existing != 0)
                {
                    remap[number] = existing;
                    _removedObjects.Add(number);
                    _objects[number] = null;
                }
                else
                {
                    _objects[number] = value;
                    matches.Add(number);
                }

                if (!dependents.TryGetValue(number, out var parents)) continue;
                foreach (int parent in parents)
                    if (--dependencies[parent] == 0) ready.Enqueue(parent, parent);
            }

            // Cyclic candidates and their ancestors are retained, never guessed equal.
            // Apply the completed map everywhere, including nested arrays, stream
            // dictionaries, pages and objects retained because of a cycle.
            for (int i = 1; i < _objects.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_objects[i] is PdfObject value)
                    _objects[i] = VisitReferences(value, Remap, cancellationToken);
            }
            return remap;
        }

        private static byte[] Serialize(PdfObject value)
        {
            using var output = new MemoryStream();
            PdfSerializer.Write(output, value);
            return output.ToArray();
        }

        private static PdfObject VisitReferences(PdfObject value, Func<PdfReference, PdfReference> visit,
            CancellationToken cancellationToken, int depth = 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > 512) throw new PdfLimitExceededException("Object nesting is too deep to write.");
            switch (value)
            {
                case PdfReference reference:
                    return visit(reference);
                case PdfArray array:
                    for (int i = 0; i < array.Count; i++)
                        array.Items[i] = VisitReferences(array[i], visit, cancellationToken, depth + 1);
                    break;
                case PdfDictionary dictionary:
                    foreach (var (key, entry) in dictionary.Entries.ToArray())
                        dictionary[key] = VisitReferences(entry, visit, cancellationToken, depth + 1);
                    break;
                case PdfStream stream:
                    VisitReferences(stream.Dictionary, visit, cancellationToken, depth + 1);
                    break;
            }
            return value;
        }

        private void WriteClassic(CountingStream counting, PdfReference root, PdfReference? info, PdfReference? encryptRef, byte[]? documentId, CancellationToken cancellationToken)
        {
            PdfSerializer.WriteAscii(counting, "%PDF-1.7\n");
            counting.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }, 0, 6);

            var offsets = new long[_objects.Count];
            for (int i = 1; i < _objects.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                offsets[i] = counting.BytesWritten;
                PdfSerializer.WriteAscii(counting, $"{i} 0 obj\n");
                PdfSerializer.Write(counting, _objects[i] ?? PdfNull.Instance);
                PdfSerializer.WriteAscii(counting, "\nendobj\n");
            }

            long xrefOffset = counting.BytesWritten;
            var xref = new StringBuilder();
            xref.Append("xref\n0 ").Append(_objects.Count).Append('\n');
            xref.Append("0000000000 65535 f\r\n");
            for (int i = 1; i < _objects.Count; i++)
                xref.Append(offsets[i].ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n\r\n");
            PdfSerializer.WriteAscii(counting, xref.ToString());

            byte[] id = documentId ?? _options.IdGenerator?.Invoke() ?? RandomNumberGenerator.GetBytes(16);
            var trailer = new PdfDictionary
            {
                ["Size"] = new PdfInteger(_objects.Count),
                ["Root"] = root,
                ["ID"] = new PdfArray(new PdfObject[] { new PdfString(id, true), new PdfString(id, true) })
            };
            if (info is not null) trailer["Info"] = info;
            if (encryptRef is not null) trailer["Encrypt"] = encryptRef;

            PdfSerializer.WriteAscii(counting, "trailer\n");
            PdfSerializer.Write(counting, trailer);
            PdfSerializer.WriteAscii(counting, $"\nstartxref\n{xrefOffset}\n%%EOF\n");
            counting.Flush();
        }

        private void WriteCompact(CountingStream counting, PdfReference root, PdfReference? info, CancellationToken cancellationToken)
        {
            PdfSerializer.WriteAscii(counting, "%PDF-1.5\n");
            counting.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }, 0, 6);

            // To map an object in an ObjStm to its exact index
            var objectToIndexInStm = new Dictionary<int, int>();
            var offsets = new long[_objects.Count];
            var objStms = new List<int>();

            var currentBatch = new List<int>();

            int excludedCatalog = root.ObjectNumber;
            // Also exclude the main Pages dictionary if we can easily find it. It's pointed to by Root.
            int excludedPagesRoot = -1;
            if (_objects[root.ObjectNumber] is PdfDictionary catDict && catDict["Pages"] is PdfReference pagesRef)
            {
                excludedPagesRoot = pagesRef.ObjectNumber;
            }
            // And any encryption dictionary would be excluded, but PowerTrove doesn't natively rewrite encryption dicts unless preserving them.

            void FlushBatch()
            {
                if (currentBatch.Count == 0) return;

                int streamNumber = Allocate();
                Array.Resize(ref offsets, _objects.Count);

                using var ms = new MemoryStream();
                using var offsetsMs = new MemoryStream();

                for (int i = 0; i < currentBatch.Count; i++)
                {
                    int objNum = currentBatch[i];
                    string offsetStr = $"{objNum} {ms.Position} ";
                    byte[] offsetBytes = Encoding.ASCII.GetBytes(offsetStr);
                    offsetsMs.Write(offsetBytes, 0, offsetBytes.Length);

                    PdfSerializer.Write(ms, _objects[objNum] ?? PdfNull.Instance);
                    ms.WriteByte((byte)'\n');
                }

                byte[] firstBytes = offsetsMs.ToArray();
                byte[] dataBytes = ms.ToArray();

                byte[] streamData = new byte[firstBytes.Length + dataBytes.Length];
                Buffer.BlockCopy(firstBytes, 0, streamData, 0, firstBytes.Length);
                Buffer.BlockCopy(dataBytes, 0, streamData, firstBytes.Length, dataBytes.Length);

                byte[] deflated = PdfFilters.Deflate(streamData);

                var dict = new PdfDictionary
                {
                    ["Type"] = new PdfName("ObjStm"),
                    ["N"] = new PdfInteger(currentBatch.Count),
                    ["First"] = new PdfInteger(firstBytes.Length),
                    ["Filter"] = new PdfName("FlateDecode")
                };

                _objects[streamNumber] = new PdfStream(dict, deflated);
                objStms.Add(streamNumber);

                for (int i = 0; i < currentBatch.Count; i++)
                {
                    offsets[currentBatch[i]] = -streamNumber; // Negative to indicate it's in an ObjStm
                    objectToIndexInStm[currentBatch[i]] = i;
                }

                currentBatch.Clear();
            }

            for (int i = 1; i < _objects.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_removedObjects.Contains(i) || i == excludedCatalog || i == excludedPagesRoot || _objects[i] is PdfStream || (_objects[i] as PdfDictionary)?.GetName("Type") == "XRef")
                {
                    continue;
                }

                currentBatch.Add(i);
                if (currentBatch.Count >= 200)
                {
                    FlushBatch();
                }
            }
            FlushBatch();

            // Write all objects (unbatched and the ObjStms themselves)
            for (int i = 1; i < _objects.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_removedObjects.Contains(i)) continue;
                if (offsets[i] < 0) continue; // It's in an ObjStm

                offsets[i] = counting.BytesWritten;
                PdfSerializer.WriteAscii(counting, $"{i} 0 obj\n");
                PdfSerializer.Write(counting, _objects[i] ?? PdfNull.Instance);
                PdfSerializer.WriteAscii(counting, "\nendobj\n");
            }

            long xrefOffset = counting.BytesWritten;

            // Build XRef stream
            int xrefObjectNumber = Allocate();
            Array.Resize(ref offsets, _objects.Count);
            offsets[xrefObjectNumber] = xrefOffset;

            // Format of W array: [1 4 2]
            // Type (1 byte), Field 2 (4 bytes), Field 3 (2 bytes)
            var xrefData = new byte[_objects.Count * 7];

            // Removed numbers are free, absent both from the file and from ObjStm
            // payloads. Link their free entries rather than emitting reference bodies.
            var nextFree = new int[_objects.Count];
            int lastFree = 0;
            foreach (int number in _removedObjects.Order())
            {
                nextFree[lastFree] = number;
                lastFree = number;
            }

            // Entry 0 (free list head)
            xrefData[0] = 0;
            WriteField(xrefData, 1, nextFree[0]);
            xrefData[5] = 255; xrefData[6] = 255; // max generation number (65535)

            for (int i = 1; i < _objects.Count; i++)
            {
                int p = i * 7;
                if (_removedObjects.Contains(i))
                {
                    // Type 0: next free object number, generation incremented from 0.
                    WriteField(xrefData, p + 1, nextFree[i]);
                    xrefData[p + 6] = 1;
                }
                else if (offsets[i] < 0)
                {
                    // Type 2: In ObjStm
                    xrefData[p] = 2;
                    int stmNum = (int)-offsets[i];
                    xrefData[p + 1] = (byte)((stmNum >> 24) & 0xFF);
                    xrefData[p + 2] = (byte)((stmNum >> 16) & 0xFF);
                    xrefData[p + 3] = (byte)((stmNum >> 8) & 0xFF);
                    xrefData[p + 4] = (byte)(stmNum & 0xFF);

                    int idx = objectToIndexInStm[i];
                    xrefData[p + 5] = (byte)((idx >> 8) & 0xFF);
                    xrefData[p + 6] = (byte)(idx & 0xFF);
                }
                else
                {
                    // Type 1: Uncompressed object
                    xrefData[p] = 1;
                    long offset = offsets[i];
                    xrefData[p + 1] = (byte)((offset >> 24) & 0xFF);
                    xrefData[p + 2] = (byte)((offset >> 16) & 0xFF);
                    xrefData[p + 3] = (byte)((offset >> 8) & 0xFF);
                    xrefData[p + 4] = (byte)(offset & 0xFF);
                    // Generation 0 for all active objects in our writer
                    xrefData[p + 5] = 0;
                    xrefData[p + 6] = 0;
                }
            }

            byte[] id = _options.IdGenerator?.Invoke() ?? RandomNumberGenerator.GetBytes(16);

            var xrefDict = new PdfDictionary
            {
                ["Type"] = new PdfName("XRef"),
                ["Size"] = new PdfInteger(_objects.Count),
                ["Root"] = root,
                ["W"] = new PdfArray(new[] { new PdfInteger(1), new PdfInteger(4), new PdfInteger(2) }),
                ["ID"] = new PdfArray(new PdfObject[] { new PdfString(id, true), new PdfString(id, true) }),
                ["Filter"] = new PdfName("FlateDecode")
            };

            if (info is not null) xrefDict["Info"] = info;

            byte[] compressedXref = PdfFilters.Deflate(xrefData);

            PdfSerializer.WriteAscii(counting, $"{xrefObjectNumber} 0 obj\n");
            PdfSerializer.Write(counting, new PdfStream(xrefDict, compressedXref));
            PdfSerializer.WriteAscii(counting, "\nendobj\n");

            PdfSerializer.WriteAscii(counting, $"\nstartxref\n{xrefOffset}\n%%EOF\n");
            counting.Flush();
        }

        private static void WriteField(byte[] data, int offset, int value)
        {
            data[offset] = (byte)(value >> 24);
            data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)value;
        }
    }

    /// <summary>Copies objects of one source document into the output table, once each.</summary>
    internal sealed class ObjectCopier
    {
        private static readonly HashSet<string> DroppedPageKeys = new(StringComparer.Ordinal) { "Parent", "B", "StructParents", "Tabs" };
        private static readonly HashSet<string> MetadataPageKeys = new(StringComparer.Ordinal) { "Metadata", "PieceInfo", "LastModified" };

        private readonly PdfDocument _source;
        private readonly ObjectTable _table;
        private readonly Dictionary<(Guid, int), int> _pages;
        private readonly Dictionary<(Guid, int), PdfImageReplacement> _replacements;
        private readonly bool _removeMetadata;
        private readonly Dictionary<int, int> _map = new();
        private readonly Queue<(int Source, int Target)> _pending = new();
        private readonly Dictionary<PdfImage, int>? _imageCache;

        private readonly PdfWriteOptions _options;
        private readonly bool _preservePageTree;

        public ObjectCopier(PdfDocument source, ObjectTable table, Dictionary<(Guid, int), int> pages, PdfWriteOptions options, Dictionary<(Guid, int), PdfImageReplacement> replacements, Dictionary<PdfImage, int>? imageCache = null, bool preservePageTree = false)
        {
            _preservePageTree = preservePageTree;
            _imageCache = imageCache;
            _source = source;
            _table = table;
            _pages = pages;
            _options = options;
            _removeMetadata = options.RemoveMetadata;
            _replacements = replacements;
        }

        public PdfDictionary CopyPage(PdfPage page, int rotationDelta, PdfReference parent, IReadOnlyList<PdfDecoration>? decorations, ObjectTable table, CancellationToken cancellationToken)
        {
            var copy = new PdfDictionary();
            bool removeMetadata = _removeMetadata || _options.Sanitize.HasFlag(PdfSanitizeFlags.Metadata) || _options.EditMetadata;
            bool removeScripts = _options.Sanitize.HasFlag(PdfSanitizeFlags.Scripts);
            foreach (var (key, value) in page.Dictionary.Entries)
            {
                if (value == null) continue;
                if (DroppedPageKeys.Contains(key)) continue;
                if (removeMetadata && MetadataPageKeys.Contains(key)) continue;
                if (removeScripts && key == "AA") continue;

                if (key == "Annots")
                {
                    if (_options.FlattenAnnotations) continue;
                    PdfObject filteredAnnots = CopyAnnots(value, 0);
                    if (filteredAnnots is PdfArray arr && arr.Items.Count > 0)
                    {
                        copy[key] = filteredAnnots;
                    }
                    else if (filteredAnnots is PdfReference)
                    {
                         copy[key] = filteredAnnots;
                    }
                    continue;
                }

                copy[key] = CopyValue(value, 0);
            }

            copy["Type"] = new PdfName("Page");
            copy["Parent"] = parent;

            // Inherited attributes become explicit because the new tree has no ancestors.
            if (copy["Resources"] is null)
            {
                if (decorations != null && decorations.Count > 0 && page.Resources is PdfReference resRef)
                {
                    PdfObject resolved = _source.GetObject(resRef.ObjectNumber) ?? new PdfDictionary();
                    copy["Resources"] = CopyValue(resolved, 0);
                }
                else
                {
                    copy["Resources"] = page.Resources is null ? new PdfDictionary() : CopyValue(page.Resources, 0);
                }
            }

            // Wait, if copy["Resources"] was not null (e.g., inherited or present in page.Dictionary.Entries)
            // It could be a PdfReference!
            if (decorations != null && decorations.Count > 0 && copy["Resources"] is PdfReference copiedResRef)
            {
                PdfReference? originalRef = page.Dictionary["Resources"] as PdfReference ?? page.Resources as PdfReference;
                if (originalRef != null)
                {
                    if (_source.GetObject(originalRef.ObjectNumber) is PdfDictionary resolved)
                    {
                        var resolvedCopy = new PdfDictionary();
                        foreach (var entry in resolved.Entries)
                        {
                            if (entry.Value is PdfReference subRef)
                            {
                                if (_source.GetObject(subRef.ObjectNumber) is PdfDictionary subDict)
                                {
                                    resolvedCopy[entry.Key] = CopyValue(subDict, 0);
                                    continue;
                                }
                            }
                            resolvedCopy[entry.Key] = CopyValue(entry.Value, 0);
                        }
                        copy["Resources"] = resolvedCopy;
                    }
                }
            }
            if (copy["MediaBox"] is null)
                copy["MediaBox"] = page.MediaBox is null
                    ? new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfInteger(612), new PdfInteger(792) })
                    : CopyValue(page.MediaBox, 0);
            if (copy["CropBox"] is null && page.CropBox is not null)
                copy["CropBox"] = CopyValue(page.CropBox, 0);

            int rotate = PdfDocument.NormalizeRotation(page.Rotate + rotationDelta);
            if (rotate == 0) copy.Remove("Rotate");
            else copy["Rotate"] = new PdfInteger(rotate);

            if (decorations != null && decorations.Count > 0)
            {
                // Fix for Issue 2: Resolve Contents array while we still have access to the source object ID
                PdfObject srcContents = page.Dictionary["Contents"] ?? PdfNull.Instance;
                if (srcContents is PdfReference cRef)
                {
                    if (_source.GetObject(cRef.ObjectNumber) is PdfArray origArray)
                    {
                        var newArray = new PdfArray();
                        foreach (var item in origArray.Items)
                        {
                            newArray.Items.Add(CopyValue(item, 0));
                        }
                        copy["Contents"] = newArray;
                    }
                }

                PdfObject rawBox = page.CropBox ?? page.MediaBox ?? new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfInteger(612), new PdfInteger(792) });
                if (rawBox is PdfReference r) rawBox = _source.GetObject(r.ObjectNumber) ?? rawBox;
                PdfArray box = rawBox as PdfArray ?? new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfInteger(612), new PdfInteger(792) });

                // If it's a reference to an array, we should evaluate the array.
                var evaluatedBox = new PdfArray();
                foreach (var item in box.Items)
                {
                    if (item is PdfReference itemRef)
                        evaluatedBox.Items.Add(_source.GetObject(itemRef.ObjectNumber) ?? item);
                    else
                        evaluatedBox.Items.Add(item);
                }

                PdfDecorationRenderer.ApplyDecorations(copy, evaluatedBox, rotate, decorations, table, this, _imageCache, _source);
            }

            if (_options.FlattenAnnotations)
                PdfAnnotationFlattener.Apply(_source, page, copy, this, table,
                    _options.KeepFlattenedLinks, cancellationToken);

            Drain(cancellationToken);
            return copy;
        }

        public void Drain(CancellationToken cancellationToken)
        {
            while (_pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (source, target) = _pending.Dequeue();

                PdfObject copiedValue;
                if (_replacements.TryGetValue((_source.InstanceId, source), out var replacement))
                {
                    var originalStream = _source.GetObject(source) as PdfStream;
                    var dict = new PdfDictionary
                    {
                        ["Type"] = new PdfName("XObject"),
                        ["Subtype"] = new PdfName("Image"),
                        ["Width"] = new PdfInteger(replacement.Width),
                        ["Height"] = new PdfInteger(replacement.Height),
                        ["BitsPerComponent"] = new PdfInteger(8),
                        ["Filter"] = new PdfName("DCTDecode")
                    };

                    string colorSpace = replacement.Components switch { 1 => "DeviceGray", 4 => "DeviceCMYK", _ => "DeviceRGB" };
                    dict["ColorSpace"] = new PdfName(colorSpace);

                    if (originalStream != null)
                    {
                        if (originalStream.Dictionary["SMask"] is PdfReference smask)
                            dict["SMask"] = CopyValue(smask, 0);
                        else if (originalStream.Dictionary["Mask"] is PdfObject mask)
                            dict["Mask"] = CopyValue(mask, 0);
                    }

                    copiedValue = new PdfStream(dict, replacement.JpegData);
                }
                else
                {
                    var value = _source.GetObject(source) ?? PdfNull.Instance;
                    copiedValue = CopyValue(value, 0);
                }

                // A damaged/missing /Type does not erase a page's identity. Repair must
                // preserve every leaf discovered through /Kids, even when its body matches another.
                bool structural = _source.IsPageObject(source) ||
                    (_preservePageTree && copiedValue is PdfDictionary dictionary && dictionary["Kids"] is not null);
                _table.Set(target, copiedValue, deduplicate: !structural);
            }
        }

        private PdfObject CopyAnnots(PdfObject annotsObj, int depth)
        {
            if (depth > 200) throw new PdfLimitExceededException("Object nesting is too deep.");

            var resolved = _source.Resolve(annotsObj) as PdfArray;
            if (resolved == null) return CopyValue(annotsObj, depth); // e.g. null or invalid

            var newArray = new PdfArray();
            bool removeScripts = _options.Sanitize.HasFlag(PdfSanitizeFlags.Scripts);
            bool removeAttachments = _options.Sanitize.HasFlag(PdfSanitizeFlags.Attachments);
            bool removeLaunch = _options.Sanitize.HasFlag(PdfSanitizeFlags.LaunchActions);
            bool removeUri = _options.Sanitize.HasFlag(PdfSanitizeFlags.UriActions);

            foreach (var item in resolved.Items)
            {
                if (item == null) continue;
                var annotDict = _source.Resolve(item) as PdfDictionary;
                if (annotDict != null)
                {
                    string? subtype = annotDict.GetName("Subtype");
                    if (removeAttachments && subtype == "FileAttachment") continue;

                    var newAnnot = new PdfDictionary();
                    foreach (var (key, entry) in annotDict.Entries)
                    {
                        if (entry == null) continue;
                        if (removeScripts && key == "AA") continue;
                        if (key == "A")
                        {
                            var aDict = _source.Resolve(entry) as PdfDictionary;
                            if (aDict != null)
                            {
                                string? s = aDict.GetName("S");
                                if (removeScripts && s == "JavaScript") continue;
                                if (removeLaunch && s == "Launch") continue;
                                if (removeUri && s == "URI") continue;
                            }
                        }

                        newAnnot[key] = CopyValue(entry, depth + 1);
                    }
                    newArray.Items.Add(newAnnot);
                }
                else
                {
                    newArray.Items.Add(CopyValue(item, depth + 1));
                }
            }
            return newArray;
        }

        public PdfObject CopyValue(PdfObject value, int depth)
        {
            if (depth > 200) throw new PdfLimitExceededException("Object nesting is too deep.");

            switch (value)
            {
                case PdfReference reference:
                    return MapReference(reference);
                case PdfArray array:
                    return new PdfArray(array.Items.Where(i => i != null).Select(item => CopyValue(item!, depth + 1)));
                case PdfDictionary dict:
                {
                    var copy = new PdfDictionary();
                    bool removeMetadata = _removeMetadata || _options.Sanitize.HasFlag(PdfSanitizeFlags.Metadata) || _options.EditMetadata;
                    bool removeScripts = _options.Sanitize.HasFlag(PdfSanitizeFlags.Scripts);
                    bool removeAttachments = _options.Sanitize.HasFlag(PdfSanitizeFlags.Attachments);

                    foreach (var (key, entry) in dict.Entries)
                    {
                        if (entry == null) continue;
                        if (removeMetadata && key == "Metadata") continue;
                        if (removeScripts && (key == "JS" || key == "AA" || key == "JavaScript")) continue;
                        if (removeAttachments && key == "EmbeddedFiles") continue;

                        // Drop JavaScript OpenAction
                        if (removeScripts && key == "OpenAction")
                        {
                             var openActionDict = _source.Resolve(entry) as PdfDictionary;
                             if (openActionDict != null && openActionDict.GetName("S") == "JavaScript") continue;
                        }

                        copy[key] = CopyValue(entry, depth + 1);
                    }
                    return copy;
                }
                case PdfStream stream:
                {
                    // /Length is recomputed on write; copying an indirect length would leave an orphan.
                    var dict = new PdfDictionary();
                    bool removeMetadata = _removeMetadata || _options.Sanitize.HasFlag(PdfSanitizeFlags.Metadata) || _options.EditMetadata;
                    foreach (var (key, entry) in stream.Dictionary.Entries)
                    {
                        if (entry == null) continue;
                        if (key == "Length" || (removeMetadata && key == "Metadata")) continue;
                        dict[key] = CopyValue(entry, depth + 1);
                    }

                    if (_options.Compact && dict["Filter"] is null)
                    {
                        byte[] deflated = PdfFilters.Deflate(stream.Data);
                        dict["Filter"] = new PdfName("FlateDecode");
                        return new PdfStream(dict, deflated);
                    }

                    return new PdfStream(dict, stream.Data);
                }
                default:
                    return value; // immutable primitives
            }
        }

        private PdfObject MapReference(PdfReference reference)
        {
            int number = reference.ObjectNumber;
            if (!_preservePageTree && _source.IsPageObject(number))
            {
                return _pages.TryGetValue((_source.InstanceId, number), out int pageNumber)
                    ? new PdfReference(pageNumber, 0)
                    : PdfNull.Instance;
            }

            if (_map.TryGetValue(number, out int mapped))
                return new PdfReference(mapped, 0);

            // Never copy page-tree nodes or the catalog via stray references.
            if (!_preservePageTree && _source.GetObject(number) is PdfDictionary d && (d.GetName("Type") is "Pages" or "Catalog"))
                return PdfNull.Instance;

            int target = _table.Allocate();
            _map[number] = target;
            _pending.Enqueue((number, target));
            return new PdfReference(target, 0);
        }
    }
}
