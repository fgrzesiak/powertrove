namespace FileKit.Imaging.Jpeg;

public static partial class JpegCoefficientCodec
{
    private sealed class Huffman
    {
        private readonly Dictionary<int, int> _symbols = new();
        public Huffman(ReadOnlySpan<byte> lengthCounts, ReadOnlySpan<byte> symbols)
        {
            int code = 0, offset = 0;
            for (int length = 1; length <= 16; length++)
            {
                int count = lengthCounts[length - 1];
                Check(code + count <= (1 << length), "Oversubscribed JPEG Huffman table.");
                for (int n = 0; n < count; n++)
                    _symbols.Add((length << 16) | code++, symbols[offset++]);
                code <<= 1;
            }
            Check(offset == symbols.Length, "Invalid JPEG Huffman table length.");
        }

        public int Read(JpegBits bits)
        {
            int code = 0;
            for (int length = 1; length <= 16; length++)
            {
                code = (code << 1) | bits.ReadBit();
                if (_symbols.TryGetValue((length << 16) | code, out int value)) return value;
            }
            throw new JpegCoefficientException("Invalid JPEG Huffman code.");
        }
    }

    private sealed class JpegBits(byte[] jpeg, int begin, int end)
    {
        private int _at = begin;
        private int _bits;
        private int _value;

        public int ReadBit()
        {
            if (_bits == 0)
            {
                Check(_at < end, "Truncated JPEG entropy scan.");
                int value = jpeg[_at++];
                if (value == 255)
                {
                    while (_at < end && jpeg[_at] == 255) _at++;
                    Check(_at < end && jpeg[_at++] == 0, "Unexpected JPEG entropy marker.");
                }
                _value = value;
                _bits = 8;
            }
            _bits--;
            return (_value >> _bits) & 1;
        }

        public int Read(int count)
        {
            Check(count is >= 0 and <= 16, "Invalid coefficient amplitude length.");
            int result = 0;
            for (int n = 0; n < count; n++) result = (result << 1) | ReadBit();
            return result;
        }

        public int ReadSigned(int count)
        {
            if (count == 0) return 0;
            int value = Read(count);
            return value < (1 << (count - 1)) ? value - ((1 << count) - 1) : value;
        }

        public void Restart(int expected)
        {
            _bits = 0;
            Check(_at + 1 < end && jpeg[_at++] == 255, "Missing JPEG restart marker.");
            while (_at < end && jpeg[_at] == 255) _at++;
            Check(_at < end && jpeg[_at++] == 0xD0 + (expected & 7), "Out-of-order JPEG restart marker.");
        }

        public bool Ended()
        {
            // Final scan padding bits are ignored. Trailing non-padding bytes
            // indicate malformed entropy data, except the final RST fill sequence.
            return _at == end;
        }
    }

    private sealed record ScanItem(JpegCoefficientComponent Component, Huffman Dc, Huffman Ac);

    public static JpegCoefficientImage Decode(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Check(input.Length is >= 6 and <= MaxInputBytes, "Invalid JPEG input size.");
        byte[] bytes = input.ToArray();
        Check(bytes[0] == 255 && bytes[1] == 0xD8, "Missing JPEG SOI marker.");

        var image = new JpegCoefficientImage();
        var dcTables = new Dictionary<int, Huffman>();
        var acTables = new Dictionary<int, Huffman>();
        int position = 2, restartInterval = 0, scans = 0;
        int headerSize = 0;
        bool haveFrame = false, ended = false;
        while (position < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(bytes[position++] == 255, "Missing JPEG marker prefix.");
            while (position < bytes.Length && bytes[position] == 255) position++;
            Check(position < bytes.Length, "Truncated JPEG marker.");
            byte marker = bytes[position++];
            if (marker == 0xD9) { ended = true; break; }
            Check(marker != 0xD8 && marker != 0x01 && !(marker >= 0xD0 && marker <= 0xD7),
                "Unexpected stand-alone JPEG marker.");
            Check(position + 2 <= bytes.Length, "Truncated JPEG segment length.");
            int length = U16(bytes, position);
            Check(length is >= 2 && position + length <= bytes.Length,
                "Invalid JPEG segment length.");
            int start = position + 2, end = position + length;
            ReadOnlySpan<byte> segment = bytes.AsSpan(start, length - 2);
            position = end;
            if (scans == 0)
            {
                headerSize += length + 2;
                Check(headerSize <= MaxHeaderBytes, "JPEG pre-scan header exceeds 1 MiB.");
            }
            switch (marker)
            {
                case 0xDB: // DQT
                {
                    int at = 0;
                    while (at < segment.Length)
                    {
                        int spec = segment[at++];
                        Check((spec >> 4) == 0 && (spec & 15) <= 3 &&
                              at + 64 <= segment.Length, "Only 8-bit JPEG quantization is supported.");
                        var values = new ushort[64];
                        for (int i = 0; i < 64; i++)
                        {
                            int value = segment[at++];
                            Check(value != 0, "Zero JPEG quantization entry.");
                            values[Zigzag[i]] = (ushort)value;
                        }
                        image.Quantization[spec & 15] = values;
                    }
                    break;
                }
                case 0xC4: // DHT
                {
                    int at = 0;
                    while (at < segment.Length)
                    {
                        int kind = segment[at++];
                        Check((kind >> 4) <= 1 && (kind & 15) <= 3 && at + 16 <= segment.Length,
                            "Invalid JPEG Huffman table id.");
                        var counts = segment.Slice(at, 16);
                        int sum = 0;
                        foreach (byte count in counts) sum += count;
                        at += 16;
                        Check(sum <= 256 && at + sum <= segment.Length, "Invalid JPEG Huffman symbols.");
                        var symbols = segment.Slice(at, sum);
                        var table = new Huffman(counts, symbols);
                        (kind >> 4 == 0 ? dcTables : acTables)[kind & 15] = table;
                        at += sum;
                    }
                    break;
                }
                case 0xC0:
                case 0xC2:
                {
                    Check(!haveFrame && segment.Length >= 9 && segment[0] == 8,
                        "Only 8-bit baseline/progressive JPEG is supported.");
                    int height = U16(segment, 1), width = U16(segment, 3);
                    int count = segment[5];
                    Check(count is >= 1 and <= 4 && segment.Length == 6 + count * 3 &&
                          width is > 0 and <= MaxDimension && height is > 0 and <= MaxDimension &&
                          (long)width * height <= MaxPixels, "Unsupported JPEG frame dimensions.");
                    image.Width = width;
                    image.Height = height;
                    image.Progressive = marker == 0xC2;
                    for (int c = 0; c < count; c++)
                    {
                        int p = 6 + c * 3;
                        int h = segment[p + 1] >> 4, v = segment[p + 1] & 15, id = segment[p];
                        Check(h is >= 1 and <= 4 && v is >= 1 and <= 4 &&
                              segment[p + 2] < 4 &&
                              image.ComponentsInternal.All(x => x.Id != id),
                            "Invalid JPEG component sampling.");
                        image.ComponentsInternal.Add(new JpegCoefficientComponent(
                            segment[p], h, v, segment[p + 2]));
                    }
                    int hmax = image.Components.Max(c => c.HorizontalSampling);
                    int vmax = image.Components.Max(c => c.VerticalSampling);
                    long coeffs = 0;
                    foreach (var component in image.Components)
                    {
                        component.BlockWidth = (int)(((long)width * component.HorizontalSampling + hmax * 8 - 1) / (hmax * 8));
                        component.BlockHeight = (int)(((long)height * component.VerticalSampling + vmax * 8 - 1) / (vmax * 8));
                        coeffs += (long)component.BlockWidth * component.BlockHeight * 64;
                    }
                    Check(coeffs <= MaxCoefficients, "JPEG coefficient limit exceeded.");
                    foreach (var c in image.ComponentsInternal)
                        c.Coefficients = new short[checked(c.BlockWidth * c.BlockHeight * 64)];
                    haveFrame = true;
                    break;
                }
                case 0xDD: // DRI
                    Check(segment.Length == 2, "Malformed restart interval.");
                    restartInterval = U16(segment, 0);
                    image.RestartInterval = restartInterval;
                    break;
                case 0xDA:
                {
                    Check(haveFrame && ++scans <= MaxScans && segment.Length >= 6,
                        "Unsupported JPEG scan count.");
                    int scanCount = segment[0];
                    Check(scanCount is >= 1 and <= 4 && segment.Length == scanCount * 2 + 4,
                        "Malformed JPEG scan components.");
                    int first = segment[^3], last = segment[^2], refinement = segment[^1];
                    int ah = refinement >> 4, al = refinement & 15;
                    bool needsDc = !image.Progressive || first == 0 && ah == 0;
                    bool needsAc = !image.Progressive || first > 0;
                    var unused = new Huffman(new byte[16], ReadOnlySpan<byte>.Empty);
                    var scan = new List<ScanItem>(scanCount);
                    for (int c = 0; c < scanCount; c++)
                    {
                        int id = segment[1 + c * 2];
                        var component = image.Components.FirstOrDefault(item => item.Id == id);
                        int selector = segment[2 + c * 2];
                        Check(component is not null && scan.All(i => i.Component != component),
                            "Unknown or duplicate JPEG scan component.");
                        int dc = selector >> 4, ac = selector & 15;
                        Check((!needsDc || dcTables.ContainsKey(dc)) &&
                              (!needsAc || acTables.ContainsKey(ac)),
                            "Missing JPEG entropy tables.");
                        scan.Add(new ScanItem(component!,
                            dcTables.GetValueOrDefault(dc) ?? unused,
                            acTables.GetValueOrDefault(ac) ?? unused));
                    }
                    Check(al <= 13 && ah <= 13, "Invalid JPEG successive approximation.");
                    if (image.Progressive)
                    {
                        Check(first <= last && last <= 63 &&
                              (first == 0 ? last == 0 : scanCount == 1) &&
                              (ah == 0 || ah == al + 1), "Invalid progressive JPEG scan parameters.");
                    }
                    else Check(first == 0 && last == 63 && ah == 0 && al == 0,
                        "Invalid sequential JPEG scan parameters.");
                    int scanEnd = FindEntropyEnd(bytes, position);
                    DecodeScan(image, scan, bytes, position, scanEnd,
                        first, last, ah, al, restartInterval, cancellationToken);
                    position = scanEnd;
                    break;
                }
                default:
                    if (marker is >= 0xE0 and <= 0xEF or 0xFE)
                    {
                        image.Metadata.Add(new JpegMarker(marker, segment.ToArray()));
                    }
                    else if (marker is >= 0xC0 and <= 0xCF or 0xCC or 0xDC)
                        throw new JpegCoefficientException("Unsupported JPEG coding process.");
                    break;
            }
        }
        Check(ended && haveFrame && scans >= 1, "JPEG EOI or frame is missing.");
        Check(image.Components.All(c => image.Quantization.ContainsKey(c.QuantizationTable)),
            "Missing JPEG quantization table.");
        return image;
    }

    private static int FindEntropyEnd(byte[] bytes, int position)
    {
        while (position + 1 < bytes.Length)
        {
            if (bytes[position++] != 255) continue;
            while (position < bytes.Length && bytes[position] == 255) position++;
            Check(position < bytes.Length, "Truncated JPEG entropy marker.");
            byte marker = bytes[position];
            if (marker == 0 || marker is >= 0xD0 and <= 0xD7) { position++; continue; }
            return position - 1;
        }
        throw new JpegCoefficientException("Truncated JPEG entropy scan.");
    }

    private static void DecodeScan(JpegCoefficientImage image, IReadOnlyList<ScanItem> scan,
        byte[] bytes, int begin, int end, int first, int last, int ah, int al,
        int restartInterval, CancellationToken ct)
    {
        var reader = new JpegBits(bytes, begin, end);
        int[] predictor = new int[image.Components.Count];
        int hmax = image.Components.Max(c => c.HorizontalSampling);
        int vmax = image.Components.Max(c => c.VerticalSampling);
        bool interleaved = scan.Count > 1;
        int countX = interleaved ? (image.Width + 8 * hmax - 1) / (8 * hmax) : scan[0].Component.BlockWidth;
        int countY = interleaved ? (image.Height + 8 * vmax - 1) / (8 * vmax) : scan[0].Component.BlockHeight;
        int eobRun = 0, mcus = 0, restart = 0;
        var discarded = new short[64];
        for (int my = 0; my < countY; my++)
            for (int mx = 0; mx < countX; mx++)
            {
                ct.ThrowIfCancellationRequested();
                if (restartInterval > 0 && mcus > 0 && mcus % restartInterval == 0)
                {
                    reader.Restart(restart++);
                    Array.Clear(predictor);
                    eobRun = 0;
                }
                foreach (var item in scan)
                {
                    var component = item.Component;
                    int horizontal = interleaved ? component.HorizontalSampling : 1;
                    int vertical = interleaved ? component.VerticalSampling : 1;
                    int index = image.ComponentsInternal.IndexOf(component);
                    for (int v = 0; v < vertical; v++)
                        for (int h = 0; h < horizontal; h++)
                        {
                            int x = mx * horizontal + h, y = my * vertical + v;
                            var block = x < component.BlockWidth && y < component.BlockHeight
                                ? component.Block(x, y) : discarded.AsSpan();
                            DecodeBlock(reader, item.Dc, item.Ac, block, ref predictor[index],
                                ref eobRun, image.Progressive, first, last, ah, al);
                        }
                }
                mcus++;
            }
        Check(reader.Ended(), "Unexpected extra JPEG scan bytes.");
    }

    private static void DecodeBlock(JpegBits bits, Huffman dc, Huffman ac,
        Span<short> block, ref int predictor, ref int eobRun, bool progressive,
        int first, int last, int ah, int al)
    {
        if (!progressive)
        {
            int diffSize = dc.Read(bits);
            Check(diffSize <= 11, "Unsupported JPEG baseline DC magnitude.");
            predictor += bits.ReadSigned(diffSize);
            block[0] = checked((short)predictor);
            ReadAcFirst(bits, ac, block, 1, 63, 0, ref eobRun, false);
            return;
        }

        if (first == 0)
        {
            if (ah == 0)
            {
                int size = dc.Read(bits);
                Check(size <= 11, "Invalid progressive DC magnitude.");
                predictor += bits.ReadSigned(size);
                block[0] = checked((short)(predictor << al));
            }
            else if (bits.ReadBit() != 0) block[0] |= (short)(1 << al);
            return;
        }

        if (ah == 0)
            ReadAcFirst(bits, ac, block, first, last, al, ref eobRun, true);
        else
            ReadAcRefine(bits, ac, block, first, last, al, ref eobRun);
    }

    private static void ReadAcFirst(JpegBits bits, Huffman ac, Span<short> block,
        int first, int last, int al, ref int eobRun, bool progressive)
    {
        if (eobRun > 0) { eobRun--; return; }
        int k = first;
        while (k <= last)
        {
            int symbol = ac.Read(bits);
            int zeros = symbol >> 4, size = symbol & 15;
            if (size == 0)
            {
                if (zeros == 15) { k += 16; continue; }
                if (progressive)
                {
                    eobRun = (1 << zeros) + bits.Read(zeros) - 1;
                }
                break;
            }
            Check(size <= 10 && k + zeros <= last, "Invalid JPEG AC coefficient run.");
            k += zeros;
            block[Zigzag[k++]] = checked((short)(bits.ReadSigned(size) << al));
        }
    }

    private static void ReadAcRefine(JpegBits bits, Huffman ac, Span<short> block,
        int first, int last, int al, ref int eobRun)
    {
        int bit = 1 << al;
        int k = first;
        if (eobRun == 0)
        {
            while (k <= last)
            {
                int symbol = ac.Read(bits);
                int zeroRun = symbol >> 4, size = symbol & 15;
                int newCoefficient = 0;
                if (size == 1) newCoefficient = bits.ReadBit() == 1 ? bit : -bit;
                else if (size != 0) throw new JpegCoefficientException("Invalid progressive refinement symbol.");
                if (size == 0 && zeroRun < 15)
                {
                    eobRun = (1 << zeroRun) + bits.Read(zeroRun);
                    break;
                }
                if (size == 0) zeroRun = 16; // ZRL.
                for (; k <= last; k++)
                {
                    int at = Zigzag[k];
                    int value = block[at];
                    if (value != 0)
                    {
                        if (bits.ReadBit() == 1 && (Math.Abs(value) & bit) == 0)
                            block[at] = checked((short)(value + (value > 0 ? bit : -bit)));
                    }
                    else if (zeroRun-- == 0)
                        break;
                }
                Check(k <= last || newCoefficient == 0, "Progressive AC placement exceeds scan.");
                if (newCoefficient != 0) block[Zigzag[k++]] = (short)newCoefficient;
            }
        }
        if (eobRun > 0)
        {
            for (; k <= last; k++)
            {
                int at = Zigzag[k], value = block[at];
                if (value != 0 && bits.ReadBit() == 1 && (Math.Abs(value) & bit) == 0)
                    block[at] = checked((short)(value + (value > 0 ? bit : -bit)));
            }
            eobRun--;
        }
    }
}
