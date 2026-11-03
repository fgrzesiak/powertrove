namespace FileKit.Imaging.Jpeg;

public static partial class JpegCoefficientCodec
{
    private sealed class JpegWriterBits(Stream destination, CancellationToken token)
    {
        private uint _value;
        private int _bits;
        private long _written;

        public void Bits(int value, int length)
        {
            token.ThrowIfCancellationRequested();
            Check(length is >= 0 and <= 16, "Invalid JPEG output bit length.");
            _value = (_value << length) | ((uint)value & ((1u << length) - 1));
            _bits += length;
            while (_bits >= 8)
            {
                _bits -= 8;
                Byte((byte)(_value >> _bits));
            }
        }

        private void Byte(byte value)
        {
            if (++_written > MaxOutputBytes) throw new JpegCoefficientException("JPEG output exceeds size limit.");
            destination.WriteByte(value);
            if (value == 255)
            {
                if (++_written > MaxOutputBytes) throw new JpegCoefficientException("JPEG output exceeds size limit.");
                destination.WriteByte(0);
            }
        }

        public void Finish()
        {
            if (_bits > 0) Bits((1 << (8 - _bits)) - 1, 8 - _bits);
            _value = 0;
        }

        public void Restart(int index)
        {
            Finish();
            token.ThrowIfCancellationRequested();
            if (_written + 2 > MaxOutputBytes)
                throw new JpegCoefficientException("JPEG output exceeds size limit.");
            destination.WriteByte(255);
            destination.WriteByte((byte)(0xD0 + (index & 7)));
            _written += 2;
        }
    }

    /// <summary>
    /// Writes a conventional 8-bit baseline JPEG of the *same quantized coefficients*.
    /// Input progressive scans are never converted through RGB pixels.
    /// </summary>
    public static byte[] Encode(JpegCoefficientImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        Check(image.Components.Count is >= 1 and <= 4 &&
              image.Width is >= 1 and <= MaxDimension &&
              image.Height is >= 1 and <= MaxDimension, "Invalid JPEG output geometry.");

        using var output = new MemoryStream();
        output.WriteByte(255); output.WriteByte(0xD8);
        foreach (var marker in image.Markers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(marker.Marker is >= 0xE0 and <= 0xEF or 0xFE,
                "Unsupported JPEG metadata marker.");
            Segment(output, marker.Marker, marker.Data);
        }

        foreach (var (key, table) in image.Quantization.OrderBy(entry => entry.Key))
        {
            Check(key is >= 0 and <= 3 && table.Length == 64 &&
                  table.All(value => value is >= 1 and <= 255),
                "Invalid JPEG quantization table.");
            var segment = new byte[65];
            segment[0] = (byte)key;
            for (int k = 0; k < 64; k++) segment[k + 1] = (byte)table[Zigzag[k]];
            Segment(output, 0xDB, segment);
        }

        var sof = new byte[6 + 3 * image.Components.Count];
        sof[0] = 8;
        W16(sof, 1, image.Height);
        W16(sof, 3, image.Width);
        sof[5] = (byte)image.Components.Count;
        for (int i = 0; i < image.Components.Count; i++)
        {
            var c = image.Components[i];
            Check(c.HorizontalSampling is >= 1 and <= 4 &&
                  c.VerticalSampling is >= 1 and <= 4 &&
                  image.Quantization.ContainsKey(c.QuantizationTable),
                "Invalid JPEG component parameters.");
            sof[6 + 3 * i] = checked((byte)c.Id);
            sof[7 + 3 * i] = (byte)((c.HorizontalSampling << 4) | c.VerticalSampling);
            sof[8 + 3 * i] = checked((byte)c.QuantizationTable);
        }
        Segment(output, 0xC0, sof);
        if (image.RestartInterval > 0)
        {
            Check(image.RestartInterval <= ushort.MaxValue, "Invalid JPEG restart interval.");
            Span<byte> interval = stackalloc byte[2];
            W16(interval, 0, image.RestartInterval);
            Segment(output, 0xDD, interval);
        }

        // Canonical complete tables avoid a dependency on Huffman tree generation.
        // All 16 DC categories use five-bit codes. The AC alphabet uses 255 nine-bit
        // codes and one ten-bit code. Neither table contains an all-ones codeword.
        var dcTable = new byte[1 + 16 + 16];
        dcTable[0] = 0;
        dcTable[5] = 16; // 16 codes of length five (count index 4 + spec byte).
        for (int i = 0; i < 16; i++) dcTable[17 + i] = (byte)i;
        Segment(output, 0xC4, dcTable);
        var acTable = new byte[1 + 16 + 256];
        acTable[0] = 0x10;
        acTable[9] = 255;  // 255 codes of length nine.
        acTable[10] = 1;   // One code of length ten.
        for (int i = 0; i < 256; i++) acTable[17 + i] = (byte)i;
        Segment(output, 0xC4, acTable);

        var sos = new byte[1 + image.Components.Count * 2 + 3];
        sos[0] = (byte)image.Components.Count;
        for (int i = 0; i < image.Components.Count; i++)
        {
            sos[1 + 2 * i] = checked((byte)image.Components[i].Id);
            sos[2 + 2 * i] = 0; // DC table zero, AC table zero.
        }
        sos[^3] = 0; sos[^2] = 63; sos[^1] = 0;
        Segment(output, 0xDA, sos);

        var writer = new JpegWriterBits(output, cancellationToken);
        int[] dcPredictors = new int[image.Components.Count];
        int maxH = image.Components.Max(c => c.HorizontalSampling);
        int maxV = image.Components.Max(c => c.VerticalSampling);
        int mcuWidth = (image.Width + 8 * maxH - 1) / (8 * maxH);
        int mcuHeight = (image.Height + 8 * maxV - 1) / (8 * maxV);
        var empty = new short[64];
        int mcus = 0, restartNumber = 0;
        void BeforeMcu()
        {
            if (image.RestartInterval > 0 && mcus > 0 &&
                mcus % image.RestartInterval == 0)
            {
                writer.Restart(restartNumber++);
                Array.Clear(dcPredictors);
            }
            mcus++;
        }

        if (image.Components.Count == 1)
        {
            var component = image.Components[0];
            for (int y = 0; y < component.BlockHeight; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x < component.BlockWidth; x++)
                {
                    BeforeMcu();
                    EncodeBlock(writer, component.Block(x, y), ref dcPredictors[0]);
                }
            }
        }
        else
        {
            for (int y = 0; y < mcuHeight; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x < mcuWidth; x++)
                {
                    BeforeMcu();
                    for (int i = 0; i < image.Components.Count; i++)
                    {
                        var component = image.Components[i];
                        for (int v = 0; v < component.VerticalSampling; v++)
                            for (int h = 0; h < component.HorizontalSampling; h++)
                            {
                                int bx = x * component.HorizontalSampling + h;
                                int by = y * component.VerticalSampling + v;
                                ReadOnlySpan<short> block = bx < component.BlockWidth && by < component.BlockHeight
                                    ? component.Block(bx, by) : empty;
                                EncodeBlock(writer, block, ref dcPredictors[i]);
                            }
                    }
                }
            }
        }
        writer.Finish();
        output.WriteByte(255); output.WriteByte(0xD9);
        Check(output.Length <= MaxOutputBytes, "JPEG output exceeds size limit.");
        return output.ToArray();
    }

    private static void EncodeBlock(JpegWriterBits writer, ReadOnlySpan<short> coefficients, ref int predictor)
    {
        int dc = coefficients[0];
        int diff = dc - predictor;
        predictor = dc;
        int dcSize = Magnitude(diff);
        Check(dcSize <= 11, "JPEG baseline DC category exceeds 11 bits.");
        writer.Bits(dcSize, 5);
        writer.Bits(Amplitude(diff, dcSize), dcSize);
        int zeroRun = 0;
        for (int k = 1; k < 64; k++)
        {
            int value = coefficients[Zigzag[k]];
            if (value == 0) { zeroRun++; continue; }
            while (zeroRun >= 16)
            {
                AcSymbol(writer, 0xF0);
                zeroRun -= 16;
            }
            int size = Magnitude(value);
            Check(size is >= 1 and <= 10, "JPEG baseline AC category exceeds 10 bits.");
            AcSymbol(writer, (zeroRun << 4) | size);
            writer.Bits(Amplitude(value, size), size);
            zeroRun = 0;
        }
        if (zeroRun > 0) AcSymbol(writer, 0);
    }

    private static int Magnitude(int number)
    {
        uint value = (uint)Math.Abs(number);
        int bits = 0;
        while (value > 0) { value >>= 1; bits++; }
        return bits;
    }

    private static int Amplitude(int number, int size) =>
        number >= 0 ? number : number - 1 + (1 << size);

    private static void AcSymbol(JpegWriterBits writer, int symbol)
    {
        if (symbol < 255) writer.Bits(symbol, 9);
        else writer.Bits(510, 10);
    }

    private static void Segment(Stream output, byte marker, ReadOnlySpan<byte> data)
    {
        Check(data.Length <= 65533 && output.Length + data.Length + 4 <= MaxOutputBytes,
            "JPEG segment exceeds size limit.");
        output.WriteByte(255); output.WriteByte(marker);
        Span<byte> len = stackalloc byte[2];
        W16(len, 0, data.Length + 2);
        output.Write(len);
        output.Write(data);
    }
}
