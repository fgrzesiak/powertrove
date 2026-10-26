using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PowerTrove.Platform.Windows.Shell;

/// <summary>PowerTrove P/action mark. SVG and runtime pixels use the same vector contours.</summary>
public static class AppIcon
{
    private const string BodyPath = "M36 16 L69 16 C94 16 108 32 108 55 C108 76 93 88 71 88 L60 88 L60 103 C60 109 57 112 51 112 L35 112 C29 112 26 109 26 103 L26 87 C26 83 28 81 31 79 L50 68 C54 66 54 63 50 61 L29 48 C27 47 26 45 26 42 L26 29 C26 20 29 16 36 16 Z M58 32 L86 49 C90 51 90 54 86 57 L58 75 C54 77 51 75 51 71 L51 36 C51 32 54 30 58 32 Z";
    private const string AccentPath = "M29 53 L48 64 C50 65 50 67 48 68 L29 79 C27 80 26 79 26 77 L26 55 C26 53 27 52 29 53 Z";
    private static readonly List<List<(double X, double Y)>> Body = Flatten(BodyPath);
    private static readonly List<List<(double X, double Y)>> Accent = Flatten(AccentPath);

    /// <summary>Canonical scalable artwork, shared with the website and installer assets.</summary>
    public static string Svg => "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 128 128\"><title>PowerTrove</title><path fill=\"#006bff\" fill-rule=\"evenodd\" d=\"" + BodyPath + "\"/><path fill=\"#62b5ff\" d=\"" + AccentPath + "\"/></svg>";

    /// <summary>Premultiplied BGRA, top-down. Tiny icons use one color to preserve their silhouette.</summary>
    public static byte[] Pixels(int size)
    {
        if (size < 16 || size > 256) throw new ArgumentOutOfRangeException(nameof(size));
        var pixels = new byte[size * size * 4];
        const int samples = 4;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double r = 0, g = 0, b = 0, alpha = 0;
            for (int sy = 0; sy < samples; sy++)
            for (int sx = 0; sx < samples; sx++)
            {
                double px = (x + (sx + 0.5) / samples) * 128 / size;
                double py = (y + (sy + 0.5) / samples) * 128 / size;
                bool accent = Contains(Accent, px, py);
                if (!accent && !Contains(Body, px, py)) continue;
                bool light = accent && size > 24;
                r += light ? 98 : 0;
                g += light ? 181 : 107;
                b += 255;
                alpha += 255;
            }
            int i = (y * size + x) * 4;
            pixels[i] = (byte)Math.Round(b / (samples * samples));
            pixels[i + 1] = (byte)Math.Round(g / (samples * samples));
            pixels[i + 2] = (byte)Math.Round(r / (samples * samples));
            pixels[i + 3] = (byte)Math.Round(alpha / (samples * samples));
        }
        return pixels;
    }

    // Only the M/L/C/Z commands used by this mark; not a general SVG renderer.
    private static List<List<(double X, double Y)>> Flatten(string path)
    {
        var tokens = Regex.Matches(path, @"[MLCZ]|-?\d+(?:\.\d+)?");
        int index = 0;
        double Number() => double.Parse(tokens[index++].Value, CultureInfo.InvariantCulture);
        var contours = new List<List<(double X, double Y)>>();
        List<(double X, double Y)> contour = null!;
        (double X, double Y) current = default;
        while (index < tokens.Count)
        {
            string command = tokens[index++].Value;
            if (command == "M")
            {
                current = (Number(), Number());
                contour = [current];
                contours.Add(contour);
            }
            else if (command == "L") { current = (Number(), Number()); contour.Add(current); }
            else if (command == "C")
            {
                var a = current;
                var b = (X: Number(), Y: Number());
                var c = (X: Number(), Y: Number());
                var d = (X: Number(), Y: Number());
                for (int step = 1; step <= 16; step++)
                {
                    double t = step / 16.0, u = 1 - t;
                    contour.Add((u * u * u * a.X + 3 * u * u * t * b.X + 3 * u * t * t * c.X + t * t * t * d.X,
                        u * u * u * a.Y + 3 * u * u * t * b.Y + 3 * u * t * t * c.Y + t * t * t * d.Y));
                }
                current = d;
            }
            else if (command != "Z") throw new InvalidOperationException("Unsupported icon contour command.");
        }
        return contours;
    }

    private static bool Contains(List<List<(double X, double Y)>> contours, double x, double y)
    {
        bool inside = false;
        foreach (var contour in contours)
        for (int i = 0, j = contour.Count - 1; i < contour.Count; j = i++)
        {
            var a = contour[i]; var b = contour[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
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
