using System.Runtime.InteropServices.WindowsRuntime;
using ContextBar.Platform.Windows.Shell;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ContextBar.Desktop;

/// <summary>The runtime-drawn ContextBar mark (<see cref="AppIcon"/>) as an image source.</summary>
internal static class AppIconImage
{
    public static WriteableBitmap Create(int size)
    {
        var bitmap = new WriteableBitmap(size, size);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            byte[] pixels = AppIcon.Pixels(size);
            stream.Write(pixels, 0, pixels.Length);
        }
        bitmap.Invalidate();
        return bitmap;
    }
}
