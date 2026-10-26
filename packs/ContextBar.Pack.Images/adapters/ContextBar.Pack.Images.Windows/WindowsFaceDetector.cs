using System.Runtime.InteropServices.WindowsRuntime;
using ContextBar.Abstractions.Files;
using ContextBar.Abstractions.Ocr;
using ContextBar.Pack.Images;
using FileKit.Imaging.Raster;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;
using Windows.Media.Ocr;

namespace ContextBar.Pack.Images.Windows;

internal static class RecognitionBitmap
{
    public static async Task<(SoftwareBitmap Bitmap, int Width, int Height)> CreateAsync(ImageRaster input, int maxSide, CancellationToken ct)
    {
        var source = new RasterImage(input.Width, input.Height, input.Bgra32);
        if (maxSide < 1) throw new InvalidDataException();
        double scale = Math.Min(1, (double)maxSide / Math.Max(input.Width, input.Height));
        int width = Math.Max(1, (int)Math.Round(input.Width * scale)), height = Math.Max(1, (int)Math.Round(input.Height * scale));
        var opaque = await Task.Run(() =>
        {
            var resized = RasterOperations.ResizeBilinear(source, width, height, ct);
            return RasterOperations.Compose(new[] { (resized, 0, 0) }, width, height, new BgraColor(255, 255, 255), ct);
        }, ct).ConfigureAwait(false);
        return (SoftwareBitmap.CreateCopyFromBuffer(opaque.Bgra.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore), width, height);
    }
}

public sealed class WindowsFaceDetector : IFaceDetector
{
    public bool IsAvailable { get { try { return FaceDetector.IsSupported && FaceDetector.IsBitmapPixelFormatSupported(BitmapPixelFormat.Gray8); } catch { return false; } } }
    public async Task<IReadOnlyList<RasterRect>> DetectAsync(ImageRaster raster, CancellationToken ct)
    {
        try
        {
            if (!IsAvailable) throw new FileProcessingException(ExtendedImageMessages.Get("FaceUnavailable"));
            var frame = await RecognitionBitmap.CreateAsync(raster, 2000, ct).ConfigureAwait(false);
            using var original = frame.Bitmap; using var gray = SoftwareBitmap.Convert(original, BitmapPixelFormat.Gray8);
            var detector = await FaceDetector.CreateAsync().AsTask(ct).ConfigureAwait(false);
            var faces = await detector.DetectFacesAsync(gray).AsTask(ct).ConfigureAwait(false);
            if (faces.Count > 1024) throw new InvalidDataException();
            return faces.Select(face =>
            {
                var box = face.FaceBox;
                var r = OcrCoordinateMapping.ToOriginal(new RecognizedWord(string.Empty, box.X, box.Y, box.Width, box.Height), raster.Width, raster.Height, frame.Width, frame.Height);
                int x = (int)Math.Floor(r.X), y = (int)Math.Floor(r.Y), right = (int)Math.Ceiling(r.X + r.Width), bottom = (int)Math.Ceiling(r.Y + r.Height);
                return new RasterRect(x, y, right - x, bottom - y);
            }).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not FileProcessingException)
        { throw new FileProcessingException(ExtendedImageMessages.Get("FaceFailed"), ex); }
    }
}
