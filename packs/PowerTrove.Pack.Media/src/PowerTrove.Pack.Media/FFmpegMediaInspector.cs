using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerTrove.Pack.Media;

public sealed class FFmpegMediaInspector(IFFmpegService service) : IMediaInspector
{
    public bool IsAvailable => service.IsProbeAvailable;
    public async Task<MediaDetails> InspectAsync(string path, CancellationToken ct)
    {
        var result = await service.ProbeAsync(["-v", "error", "-protocol_whitelist", "file", "-print_format", "json", "-show_streams", "-show_format", "-show_data_hash", "sha256", Path.GetFullPath(path)], ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!result.Success) throw MediaMessages.InspectFailed();
        return Parse(result.Output);
    }
    public static MediaDetails Parse(string json)
    {
        try
        {
            if (json.Length > 1048576) throw new JsonException();
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = doc.RootElement;
            if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array || streams.GetArrayLength() > 64) throw new JsonException();
            JsonElement? video = null, audio = null;
            foreach (var stream in streams.EnumerateArray())
            {
                string? kind = Text(stream, "codec_type");
                if (kind == "video" && !(stream.TryGetProperty("disposition", out var disposition) && Number(disposition, "attached_pic") == 1)) video ??= stream;
                if (kind == "audio") audio ??= stream;
            }
            root.TryGetProperty("format", out var format);
            double duration = Number(format, "duration") ?? Math.Max(video is { } v ? Number(v, "duration") ?? 0 : 0, audio is { } a ? Number(a, "duration") ?? 0 : 0);
            if (!double.IsFinite(duration) || duration <= 0 || duration > 7 * 24 * 3600) throw new JsonException();
            int? width = Integer(video, "width", 32768), height = Integer(video, "height", 32768);
            if (width.HasValue != height.HasValue || (long)(width ?? 0) * (height ?? 0) > 80000000) throw new JsonException();
            double rotation = 0;
            if (video is { } rotated)
            {
                if (rotated.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
                    foreach (var side in sideData.EnumerateArray()) rotation = Number(side, "rotation") ?? rotation;
                else if (rotated.TryGetProperty("tags", out var tags)) rotation = Number(tags, "rotate") ?? 0;
                if (Math.Abs(rotation % 180) is > 89 and < 91) (width, height) = (height, width);
            }
            string? videoCodec = video is { } vv ? MediaTechnical.Codec(Text(vv, "codec_name")) : null;
            string? audioCodec = audio is { } aa ? MediaTechnical.Codec(Text(aa, "codec_name")) : null;
            double? rate = video is { } vr ? Rational(Text(vr, "avg_frame_rate")) ?? Rational(Text(vr, "r_frame_rate")) : null;
            // A copy signature includes extradata hashes and all stream parameters; missing
            // hashes mean re-encode rather than guessing that codecs are compatible.
            string? signature = null;
            if ((video is null || Text(video.Value, "extradata_hash") is not null) && (audio is null || Text(audio.Value, "extradata_hash") is not null))
            {
                var fields = new[] { "codec_type", "codec_name", "codec_tag_string", "profile", "level", "width", "height", "pix_fmt", "sample_fmt", "sample_rate", "channels", "channel_layout", "time_base", "r_frame_rate", "extradata_hash" };
                string data = rotation.ToString("R", CultureInfo.InvariantCulture) + "|" + string.Join("|", streams.EnumerateArray().Select(s => string.Join(";", fields.Select(f => Text(s, f) ?? ""))));
                signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
            }
            double? bits = Number(format, "bit_rate");
            return new(TimeSpan.FromSeconds(duration), width, height, rate, videoCodec, audioCodec,
                bits is > 0 and < 1000000000000 ? (long)bits.Value : null, Integer(audio, "channels", 64), Integer(audio, "sample_rate", 768000), audio.HasValue, signature);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException or ArgumentException)
        { throw MediaMessages.InspectFailed(ex); }
    }
    private static string? Text(JsonElement obj, string name) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;
    private static double? Number(JsonElement obj, string name) => double.TryParse(Text(obj, name), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) ? n : null;
    private static int? Integer(JsonElement? obj, string name, int max) => obj is { } o && Number(o, name) is { } n && n >= 1 && n <= max && n == Math.Truncate(n) ? (int)n : null;
    private static double? Rational(string? value)
    {
        var p = value?.Split('/');
        if (p is not { Length: 2 } || !double.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out double numerator) || !double.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out double denominator) || denominator == 0) return null;
        double result = numerator / denominator; return result is > 0 and < 10000 ? result : null;
    }
}

