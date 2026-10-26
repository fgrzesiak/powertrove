using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using ContextBar.Pack.Pdf.Windows;
using Windows.Graphics.Imaging;
using Xunit;

namespace ContextBar.Platform.Windows.Tests;

public class WicImageToJpegConverterTests
{
    [Fact]
    public async Task ConvertsAGeneratedBmpToJpeg()
    {
        if (!OperatingSystem.IsWindows())
            return; // WIC is a Windows component.
        string path = Path.Combine(Path.GetTempPath(), $"cb-{Guid.NewGuid():N}.bmp");
        try
        {
            File.WriteAllBytes(path, Bmp(8, 6));
            var converter = new WicImageToJpegConverter();
            Assert.True(converter.CanConvert("bmp"));

            using var jpeg = new MemoryStream();
            await converter.ConvertAsync(path, "bmp", jpeg, CancellationToken.None);

            byte[] bytes = jpeg.ToArray();
            Assert.True(bytes.Length > 100);
            Assert.Equal(0xFF, bytes[0]);
            Assert.Equal(0xD8, bytes[1]); // JPEG SOI marker
            jpeg.Position = 0;
            using var encoded = jpeg.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(encoded);
            Assert.Equal(8u, decoder.PixelWidth);
            Assert.Equal(6u, decoder.PixelHeight);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] Bmp(int width, int height)
    {
        int row = (width * 3 + 3) & ~3;
        int size = 54 + row * height;
        var data = new byte[size];
        data[0] = (byte)'B'; data[1] = (byte)'M';
        BitConverter.GetBytes(size).CopyTo(data, 2);
        BitConverter.GetBytes(54).CopyTo(data, 10);
        BitConverter.GetBytes(40).CopyTo(data, 14);
        BitConverter.GetBytes(width).CopyTo(data, 18);
        BitConverter.GetBytes(height).CopyTo(data, 22);
        BitConverter.GetBytes((short)1).CopyTo(data, 26);
        BitConverter.GetBytes((short)24).CopyTo(data, 28);
        for (int i = 54; i < size; i++) data[i] = (byte)(i * 7);
        return data;
    }
}
