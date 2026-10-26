using System.Buffers.Binary;
using BitMiracle.LibJpeg.Classic;
using ContextBar.Abstractions.Files;
using ContextBar.Pack.Images;
using FileKit.Imaging;

namespace ContextBar.Pack.Images.Magick;

/// <summary>Transforms quantized coefficients directly. Never decodes/re-encodes RGB pixels or trims edge MCUs.</summary>
public sealed class CoefficientJpegTransformer : IImageLosslessTransformer
{
    public Task TransformAsync(Stream source, Stream destination, LosslessImageRequest request, CancellationToken ct) => Task.Run(() =>
    {
        var decoder = new jpeg_decompress_struct(new PrivateErrors());
        var encoder = new jpeg_compress_struct(new PrivateErrors());
        try
        {
            if (!source.CanSeek || source.Length - source.Position > ImageLimits.Default.MaxFileBytes) throw Failure("JpegUnsupported");
            long start = source.Position;
            byte[] header = new byte[ImageProbe.MaxHeaderBytes]; int count = 0;
            while (count < header.Length)
            {
                ct.ThrowIfCancellationRequested(); int read = source.Read(header, count, header.Length - count); if (read == 0) break; count += read;
            }
            var info = ImageProbe.Probe(header.AsSpan(0, count));
            if (info.Format != ImageFormat.Jpeg || info.PixelCount > 60_000_000 || info.Width > 30000 || info.Height > 30000 || info.Orientation != request.ExpectedOrientation)
                throw Failure("JpegUnsupported");
            var quantization = ReadQuantization(header.AsSpan(0, count)); source.Position = start;
            var progress = new jpeg_progress_mgr(); progress.OnProgress += (_, _) => { ct.ThrowIfCancellationRequested(); if (decoder.Input_scan_number > 32) throw Failure("JpegUnsupported"); }; decoder.Progress = progress; encoder.Progress = progress;
            decoder.jpeg_stdio_src(new CancellationStream(source, ct));
            for (int marker = 0xE0; marker <= 0xEF; marker++) decoder.jpeg_save_markers(marker, 65535);
            decoder.jpeg_save_markers(0xFE, 65535); decoder.jpeg_read_header(true);
            if (decoder.Data_precision != 8 || decoder.Num_components is < 1 or > 4 || decoder.Image_width != info.Width || decoder.Image_height != info.Height)
                throw Failure("JpegUnsupported");
            int maxH = decoder.Comp_info.Max(c => c.H_samp_factor), maxV = decoder.Comp_info.Max(c => c.V_samp_factor);
            if (maxH is < 1 or > 4 || maxV is < 1 or > 4 || decoder.Comp_info.Any(c => c.H_samp_factor is < 1 or > 4 || c.V_samp_factor is < 1 or > 4)) throw Failure("JpegUnsupported");
            if (request.Transform != OrthogonalTransform.Identity && (info.Width % (8 * maxH) != 0 || info.Height % (8 * maxV) != 0))
                throw Failure("JpegPartialMcu");
            long coefficients = decoder.Comp_info.Sum(c => (long)((info.Width * c.H_samp_factor + maxH * 8 - 1) / (maxH * 8)) * ((info.Height * c.V_samp_factor + maxV * 8 - 1) / (maxV * 8)) * 64);
            if (coefficients > 96_000_000) throw Failure("JpegUnsupported");
            var input = decoder.jpeg_read_coefficients(); ct.ThrowIfCancellationRequested();
            decoder.jpeg_copy_critical_parameters(encoder);
            bool transpose = ImageTransforms.SwapsAxes(request.Transform);
            if (transpose)
            {
                // LibJpeg's public Image_width setter does not update its internal transcode dimensions.
                // A header-only decoder supplies the transposed dimensions/sampling through the public copy API.
                var shape = new jpeg_decompress_struct(new PrivateErrors());
                try
                {
                    using var parameters = new MemoryStream(TransposeHeader(header.AsSpan(0, count)));
                    shape.jpeg_stdio_src(parameters); shape.jpeg_read_header(true); shape.jpeg_calc_output_dimensions();
                    shape.jpeg_copy_critical_parameters(encoder);
                }
                finally { shape.jpeg_destroy(); }
            }
            foreach (var (table, original) in quantization)
            {
                var values = new int[64];
                for (int v = 0; v < 8; v++) for (int u = 0; u < 8; u++) values[v * 8 + u] = original[transpose ? u * 8 + v : v * 8 + u];
                encoder.jpeg_add_quant_table(table, values, 100, false);
            }
            jvirt_array<JBLOCK>[] output = input;
            if (request.Transform != OrthogonalTransform.Identity)
            {
                output = new jvirt_array<JBLOCK>[decoder.Num_components];
                for (int c = 0; c < output.Length; c++)
                {
                    int width = info.Width / (8 * maxH) * decoder.Comp_info[c].H_samp_factor;
                    int height = info.Height / (8 * maxV) * decoder.Comp_info[c].V_samp_factor;
                    output[c] = jpeg_common_struct.CreateBlocksArray(transpose ? height : width, transpose ? width : height);
                    var destinationRows = output[c].Access(0, transpose ? width : height);
                    for (int y = 0; y < height; y++)
                    {
                        ct.ThrowIfCancellationRequested(); var row = input[c].Access(y, 1)[0];
                        for (int x = 0; x < width; x++)
                        {
                            var (dx, dy) = ImageTransforms.Map(x, y, width, height, request.Transform);
                            var block = destinationRows[dy][dx];
                            for (int v = 0; v < 8; v++) for (int u = 0; u < 8; u++)
                            {
                                int value = row[x][transpose ? u * 8 + v : v * 8 + u];
                                bool negative = request.Transform switch {
                                    OrthogonalTransform.Rotate90 or OrthogonalTransform.FlipHorizontal => (u & 1) != 0,
                                    OrthogonalTransform.Rotate270 or OrthogonalTransform.FlipVertical => (v & 1) != 0,
                                    OrthogonalTransform.Rotate180 or OrthogonalTransform.Transverse => ((u + v) & 1) != 0, _ => false };
                                block[v * 8 + u] = checked((short)(negative ? -value : value));
                            }
                        }
                    }
                }
            }
            encoder.Optimize_coding = true;
            // APP markers are copied once below, preserving JFIF/Adobe as well as private metadata.
            encoder.Write_JFIF_header = false; encoder.Write_Adobe_marker = false;
            encoder.jpeg_stdio_dest(new CancellationStream(destination, ct)); encoder.jpeg_write_coefficients(output);
            foreach (var marker in decoder.Marker_list)
            {
                ct.ThrowIfCancellationRequested();
                encoder.jpeg_write_marker(marker.Marker, JpegMetadata.Normalize(marker.Marker, marker.Data, encoder.Image_width, encoder.Image_height, transpose));
            }
            encoder.jpeg_finish_compress(); decoder.jpeg_finish_decompress(); ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not FileProcessingException)
        { throw new FileProcessingException(ExtendedImageMessages.Get("JpegFailed"), ex); }
        finally { encoder.jpeg_destroy(); decoder.jpeg_destroy(); }
    }, ct);

    private static readonly int[] Zigzag = { 0,1,8,16,9,2,3,10,17,24,32,25,18,11,4,5,12,19,26,33,40,48,41,34,27,20,13,6,7,14,21,28,35,42,49,56,57,50,43,36,29,22,15,23,30,37,44,51,58,59,52,45,38,31,39,46,53,60,61,54,47,55,62,63 };
    private static byte[] TransposeHeader(ReadOnlySpan<byte> source)
    {
        byte[] header = source.ToArray(); int at = 2;
        for (int guard = 0; guard < 65536 && at + 4 <= header.Length; guard++)
        {
            if (header[at++] != 255) throw Failure("JpegUnsupported"); byte marker;
            do { if (at >= header.Length) throw Failure("JpegUnsupported"); marker = header[at++]; } while (marker == 255);
            if (at + 2 > header.Length) throw Failure("JpegUnsupported");
            int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(at)); if (length < 2 || at + length > header.Length) throw Failure("JpegUnsupported");
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (length < 8 || length != 8 + 3 * header[at + 7]) throw Failure("JpegUnsupported");
                (header[at + 3], header[at + 5]) = (header[at + 5], header[at + 3]);
                (header[at + 4], header[at + 6]) = (header[at + 6], header[at + 4]);
                for (int c = 0; c < header[at + 7]; c++) { int p = at + 9 + c * 3; header[p] = (byte)((header[p] << 4) | (header[p] >> 4)); }
                return header;
            }
            if (marker == 0xDA) throw Failure("JpegUnsupported"); at += length;
        }
        throw Failure("JpegUnsupported");
    }
    private static Dictionary<int, int[]> ReadQuantization(ReadOnlySpan<byte> header)
    {
        var result = new Dictionary<int, int[]>(); int at = 2;
        for (int guard = 0; guard < 65536 && at + 4 <= header.Length; guard++)
        {
            if (header[at++] != 255) throw Failure("JpegUnsupported"); byte marker;
            do { if (at >= header.Length) throw Failure("JpegUnsupported"); marker = header[at++]; } while (marker == 255);
            if (marker == 0xDA) return result.Count > 0 ? result : throw Failure("JpegUnsupported");
            int length = BinaryPrimitives.ReadUInt16BigEndian(header[at..]);
            if (length < 2 || at + length > header.Length) throw Failure("JpegUnsupported");
            if (marker == 0xDB)
            {
                int p = at + 2;
                while (p < at + length)
                {
                    int table = header[p++]; if ((table >> 4) != 0 || (table & 15) > 3 || p + 64 > at + length) throw Failure("JpegUnsupported");
                    var values = new int[64]; for (int i = 0; i < 64; i++) { int value = header[p++]; if (value == 0) throw Failure("JpegUnsupported"); values[Zigzag[i]] = value; } result[table & 15] = values;
                }
            }
            at += length;
        }
        throw Failure("JpegUnsupported");
    }
    private static FileProcessingException Failure(string key) => new(ExtendedImageMessages.Get(key));
    private sealed class PrivateErrors : jpeg_error_mgr
    {
        public override void output_message() { }
        public override void emit_message(int level) { if (level < 0) throw Failure("JpegFailed"); }
        public override void error_exit() => throw Failure("JpegFailed");
    }
    private sealed class CancellationStream(Stream inner, CancellationToken ct) : Stream
    {
        private long _written;
        private long _read;
        public override int Read(byte[] buffer, int offset, int count) { ct.ThrowIfCancellationRequested(); int n = inner.Read(buffer, offset, count); _read += n; if (_read > ImageLimits.Default.MaxFileBytes) throw Failure("JpegUnsupported"); return n; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            ct.ThrowIfCancellationRequested(); _written += count; if (_written > ImageLimits.Default.MaxFileBytes) throw Failure("JpegUnsupported"); inner.Write(buffer, offset, count);
        }
        public override void Flush() { ct.ThrowIfCancellationRequested(); inner.Flush(); }
        public override bool CanRead => inner.CanRead; public override bool CanWrite => inner.CanWrite; public override bool CanSeek => inner.CanSeek;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin); public override void SetLength(long value) => inner.SetLength(value);
    }
}
