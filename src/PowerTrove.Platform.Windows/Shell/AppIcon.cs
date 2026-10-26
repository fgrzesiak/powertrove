using System;
using System.Runtime.InteropServices;

namespace PowerTrove.Platform.Windows.Shell;

/// <summary>
/// Draws the PowerTrove mark at runtime (a blue rounded square with a white search bar), so the
/// tray and window icons need no binary asset. <see cref="Pixels"/> is pure and testable.
/// </summary>
public static class AppIcon
{
    /// <summary>Returns premultiplied BGRA pixels, top-down, of a size x size icon.</summary>
    public static byte[] Pixels(int size)
    {
        if (size < 16 || size > 256) throw new ArgumentOutOfRangeException(nameof(size));
        var pixels = new byte[size * size * 4];
        double scale = size / 32.0;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double px = (x + 0.5) / scale, py = (y + 0.5) / scale; // 32-unit design grid
            double tile = Coverage(RoundedRect(px, py, 1, 1, 31, 31, 7));
            double bar = Coverage(RoundedRect(px, py, 6, 12.5, 26, 19.5, 3.5));
            double dot = Coverage(RoundedRect(px, py, 8.5, 14.5, 11.5, 17.5, 1.5));
            // Tile #2563EB, bar white, dot in tile color.
            double white = bar * (1 - dot);
            double alpha = tile;
            double r = (0x25 * (1 - white) + 255 * white) * alpha;
            double g = (0x63 * (1 - white) + 255 * white) * alpha;
            double b = (0xEB * (1 - white) + 255 * white) * alpha;
            int i = (y * size + x) * 4;
            pixels[i] = (byte)Math.Round(b);
            pixels[i + 1] = (byte)Math.Round(g);
            pixels[i + 2] = (byte)Math.Round(r);
            pixels[i + 3] = (byte)Math.Round(alpha * 255);
        }
        return pixels;
    }

    /// <summary>Creates an HICON of the given size; the caller owns it (DestroyIcon).</summary>
    public static IntPtr CreateHandle(int size)
    {
        byte[] pixels = Pixels(size);
        var header = new BITMAPV5HEADER
        {
            bV5Size = (uint)Marshal.SizeOf<BITMAPV5HEADER>(),
            bV5Width = size,
            bV5Height = -size, // top-down
            bV5Planes = 1,
            bV5BitCount = 32,
            bV5Compression = 3, // BI_BITFIELDS
            bV5RedMask = 0x00FF0000,
            bV5GreenMask = 0x0000FF00,
            bV5BlueMask = 0x000000FF,
            bV5AlphaMask = 0xFF000000,
            bV5Endpoints = new byte[36],
        };
        IntPtr color = CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero) return IntPtr.Zero;
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        IntPtr mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        try
        {
            var info = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            DeleteObject(color);
            DeleteObject(mask);
        }
    }

    private static double RoundedRect(double x, double y, double left, double top, double right, double bottom, double radius)
    {
        // Signed distance to a rounded rectangle (negative inside).
        double cx = (left + right) / 2, cy = (top + bottom) / 2;
        double hx = (right - left) / 2 - radius, hy = (bottom - top) / 2 - radius;
        double dx = Math.Max(Math.Abs(x - cx) - hx, 0), dy = Math.Max(Math.Abs(y - cy) - hy, 0);
        double outside = Math.Sqrt(dx * dx + dy * dy);
        double inside = Math.Min(Math.Max(Math.Abs(x - cx) - hx, Math.Abs(y - cy) - hy), 0);
        return outside + inside - radius;
    }

    private static double Coverage(double distance) => Math.Clamp(0.5 - distance, 0, 1);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPV5HEADER
    {
        public uint bV5Size; public int bV5Width; public int bV5Height; public ushort bV5Planes; public ushort bV5BitCount;
        public uint bV5Compression; public uint bV5SizeImage; public int bV5XPelsPerMeter; public int bV5YPelsPerMeter;
        public uint bV5ClrUsed; public uint bV5ClrImportant; public uint bV5RedMask; public uint bV5GreenMask;
        public uint bV5BlueMask; public uint bV5AlphaMask; public uint bV5CSType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)] public byte[] bV5Endpoints;
        public uint bV5GammaRed; public uint bV5GammaGreen; public uint bV5GammaBlue; public uint bV5Intent;
        public uint bV5ProfileData; public uint bV5ProfileSize; public uint bV5Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPV5HEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
}
