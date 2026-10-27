using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PowerTrove.Pack.Media;

public sealed partial class FFmpegMediaEngine
{
    private static List<string> BaseArguments() => ["-hide_banner", "-nostdin", "-nostats", "-n"];
    private static void Input(List<string> args, string path) => args.AddRange(["-protocol_whitelist", "file", "-i", Path.GetFullPath(path)]);
    private static string Option(MediaRequest r, string key, string fallback = "") => r.Options?.TryGetValue(key, out var value) == true ? value : fallback;
    private static bool Flag(MediaRequest r, string key, bool fallback = false) => bool.TryParse(Option(r, key), out bool value) ? value : fallback;
    private static int Int(MediaRequest r, string key, int fallback) => int.TryParse(Option(r, key), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : fallback;
    private static TimeSpan Time(MediaRequest r, string key, string fallback = "0") => MediaTime.TryParse(Option(r, key, fallback), out var time) ? time : throw MediaMessages.ProcessingFailed();
    private async Task RunAsync(List<string> args, CancellationToken ct)
    {
        var result = await _ffmpeg.ExecuteAsync(args, ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        if (!result.Success) throw MediaMessages.ProcessingFailed();
    }
    private async Task ProcessAdvancedAsync(string source, string destination, MediaRequest r, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (r.Operation)
        {
            case MediaOperation.NormalizeAudio: await NormalizeAsync(source, destination, r, ct).ConfigureAwait(false); return;
            case MediaOperation.Merge: await MergeAsync(source, destination, r, ct).ConfigureAwait(false); return;
            case MediaOperation.ToGif: await GifAsync(source, destination, r, ct).ConfigureAwait(false); return;
            case MediaOperation.ExtractFrames: await FramesAsync(source, destination, r, ct).ConfigureAwait(false); return;
        }
        var args = BaseArguments();
        if (r.Operation == MediaOperation.Trim && !Flag(r, "Precise"))
        {
            args.AddRange(["-ss", MediaTime.Seconds(Time(r, "Start"))]);
            if (Option(r, "End").Length > 0) args.AddRange(["-to", MediaTime.Seconds(Time(r, "End"))]);
            Input(args, source); args.AddRange(["-c", "copy"]);
        }
        else if (r.Operation == MediaOperation.RotateVideo && Flag(r, "MetadataOnly"))
        {
            int angle = Rotation(r);
            args.AddRange(["-noautorotate", "-display_rotation", (-angle).ToString(CultureInfo.InvariantCulture)]); Input(args, source);
            args.AddRange(["-map", "0:V:0", "-map", "0:a:0?", "-c", "copy", "-metadata:s:v:0", "rotate=" + (-angle).ToString(CultureInfo.InvariantCulture)]);
        }
        else
        {
            Input(args, source);
            if (r.Operation == MediaOperation.Trim)
            {
                var start = Time(r, "Start"); args.AddRange(["-ss", MediaTime.Seconds(start)]);
                if (Option(r, "End").Length > 0) args.AddRange(["-t", MediaTime.Seconds(Time(r, "End") - start)]);
                args.AddRange(EncodeFor(r.SourceExtension));
            }
            else if (r.Operation == MediaOperation.CompressVideo)
            {
                var info = Details(r); var size = MediaPlanning.CompressionSize(info.Width ?? 0, info.Height ?? 0, Option(r, "Quality", "medium"));
                int targetMb = Int(r, "TargetSizeMb", 0);
                if (targetMb > 0) size = size with { Bitrate = MediaPlanning.TargetVideoBitrate(info.Duration, targetMb * 1000000L, Flag(r, "KeepAudio", true) && info.HasAudio, size.Bitrate) };
                args.AddRange(["-map", "0:V:0", "-vf", $"scale={size.Width}:{size.Height}", "-c:v", "libx264", "-preset", "fast", "-pix_fmt", "yuv420p", "-b:v", size.Bitrate.ToString(CultureInfo.InvariantCulture), "-maxrate", size.Bitrate.ToString(CultureInfo.InvariantCulture), "-bufsize", (size.Bitrate * 2).ToString(CultureInfo.InvariantCulture)]);
                args.AddRange(Flag(r, "KeepAudio", true) ? ["-map", "0:a:0?", "-c:a", "aac", "-b:a", "128k"] : ["-an"]);
            }
            else
            {
                int angle = Rotation(r); string filter = angle == 180 ? "hflip,vflip" : angle == 90 ? "transpose=1" : "transpose=2";
                args.AddRange(["-map", "0:V:0", "-map", "0:a:0?", "-vf", filter + ",scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-metadata:s:v:0", "rotate=0"]);
            }
        }
        if (r.SourceExtension == ".m4v" && r.Operation == MediaOperation.Trim) args.AddRange(["-f", "mp4"]);
        args.Add(destination); await RunAsync(args, ct).ConfigureAwait(false);
    }
    private static int Rotation(MediaRequest r)
    {
        int angle = Int(r, "Angle", 90);
        return angle is 90 or 180 or 270 ? angle : throw MediaMessages.ProcessingFailed();
    }
    private static MediaDetails Details(MediaRequest r) => r.InputDetails is { Count: > 0 } ? r.InputDetails[0] : throw MediaMessages.InspectFailed();
    private static IEnumerable<string> EncodeFor(string extension) => MediaAction.AudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ? AudioCodec(extension) : extension switch
    {
        ".webm" => ["-c:v", "libvpx-vp9", "-c:a", "libopus"], ".wmv" => ["-c:v", "wmv2", "-c:a", "wmav2"], ".avi" => ["-c:v", "mpeg4", "-c:a", "libmp3lame"],
        _ => ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac"]
    };
    private static string[] AudioCodec(string extension) => extension switch
    {
        ".wav" => ["-vn", "-c:a", "pcm_s16le"], ".flac" => ["-vn", "-c:a", "flac"], ".mp3" => ["-vn", "-c:a", "libmp3lame", "-q:a", "2"],
        ".ogg" => ["-vn", "-c:a", "libvorbis"], ".wma" => ["-vn", "-c:a", "wmav2"], _ => ["-vn", "-c:a", "aac", "-b:a", "192k"]
    };
    public sealed record Loudness(double Integrated, double Peak, double Range, double Threshold, double Offset);
    public static Loudness ParseLoudness(string output)
    {
        try
        {
            if (output.Length > 1048576) throw new JsonException();
            int begin = output.LastIndexOf('{'), end = output.LastIndexOf('}'); if (begin < 0 || end < begin || end - begin > 65536) throw new JsonException();
            using var doc = JsonDocument.Parse(output[begin..(end + 1)], new JsonDocumentOptions { MaxDepth = 8 });
            double Value(string key, double min, double max)
            {
                if (!doc.RootElement.TryGetProperty(key, out var raw) || !double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value < min || value > max) throw new JsonException();
                return value;
            }
            return new(Value("input_i", -99, 0), Value("input_tp", -99, 99), Value("input_lra", 0, 99), Value("input_thresh", -99, 0), Value("target_offset", -99, 99));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw new FileProcessingException(SR.GetString("MediaLoudnessFailed"), ex); }
    }
    private async Task NormalizeAsync(string source, string destination, MediaRequest r, CancellationToken ct)
    {
        var first = BaseArguments(); Input(first, source); first.AddRange(["-vn", "-af", "loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json", "-f", "null", "-"]);
        var result = await _ffmpeg.ExecuteAsync(first, ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); if (!result.Success) throw MediaMessages.ProcessingFailed();
        var m = ParseLoudness(result.Error);
        string N(double n) => n.ToString("R", CultureInfo.InvariantCulture);
        string filter = $"loudnorm=I=-16:TP=-1.5:LRA=11:measured_I={N(m.Integrated)}:measured_TP={N(m.Peak)}:measured_LRA={N(m.Range)}:measured_thresh={N(m.Threshold)}:offset={N(m.Offset)}:linear=true:print_format=summary";
        var second = BaseArguments(); Input(second, source); second.AddRange(["-af", filter, "-ar", "48000"]); second.AddRange(AudioCodec(r.SourceExtension)); second.Add(destination);
        await RunAsync(second, ct).ConfigureAwait(false);
    }
    private async Task GifAsync(string source, string destination, MediaRequest r, CancellationToken ct)
    {
        var info = Details(r); var start = Time(r, "Start"); int fps = Int(r, "Fps", 12), width = Int(r, "Width", 480);
        var times = MediaPlanning.GifTimes(start, TimeSpan.FromSeconds(Int(r, "Duration", 5)), fps, info.Duration);
        var size = MediaPlanning.GifSize(info.Width ?? 0, info.Height ?? 0, width);
        using var temp = new MediaTempDirectory(); string palette = Path.Combine(temp.Path, "palette.png");
        string filter = $"fps={fps},scale={size.Width}:{size.Height}:flags=lanczos";
        string duration = MediaTime.Seconds(TimeSpan.FromSeconds(Math.Min(Int(r, "Duration", 5), (info.Duration - start).TotalSeconds)));
        var first = BaseArguments(); first.AddRange(["-ss", MediaTime.Seconds(start)]); Input(first, source); first.AddRange(["-t", duration, "-vf", filter + ",palettegen", "-frames:v", "1", "-update", "1", palette]);
        await RunAsync(first, ct).ConfigureAwait(false);
        var second = BaseArguments(); second.AddRange(["-ss", MediaTime.Seconds(start)]); Input(second, source); Input(second, palette);
        second.AddRange(["-t", duration, "-filter_complex", $"[0:V:0]{filter}[v];[v][1:v:0]paletteuse", "-frames:v", times.Count.ToString(CultureInfo.InvariantCulture), "-loop", "0", destination]);
        await RunAsync(second, ct).ConfigureAwait(false);
    }
    private async Task FramesAsync(string source, string destination, MediaRequest r, CancellationToken ct)
    {
        var info = Details(r); bool single = Option(r, "Mode", "single") == "single";
        var times = MediaPlanning.FrameTimes(info.Duration, single, single ? Time(r, "At") : TimeSpan.Zero, single ? TimeSpan.FromSeconds(1) : Time(r, "EverySeconds", "1"));
        string format = Option(r, "Format", "jpg"); if (format is not ("jpg" or "png")) throw MediaMessages.ProcessingFailed();
        var args = BaseArguments(); if (single) args.AddRange(["-ss", MediaTime.Seconds(times[0])]); Input(args, source); args.AddRange(["-map", "0:V:0"]);
        if (!single) args.AddRange(["-vf", "fps=fps=1/" + MediaTime.Seconds(Time(r, "EverySeconds", "1")) + ":start_time=0:round=near"]);
        args.AddRange(["-frames:v", times.Count.ToString(CultureInfo.InvariantCulture), "-start_number", "0", Path.Combine(destination, "frame-%06d." + format)]);
        await RunAsync(args, ct).ConfigureAwait(false);
        int count = 0;
        for (int i = 0; i < times.Count; i++)
        {
            ct.ThrowIfCancellationRequested(); string numbered = Path.Combine(destination, "frame-" + i.ToString("000000", CultureInfo.InvariantCulture) + "." + format);
            if (!File.Exists(numbered)) break;
            File.Move(numbered, Path.Combine(destination, MediaPlanning.FrameName(Option(r, "Stem", "frame"), times[i], format))); count++;
        }
        if (count == 0) throw MediaMessages.ProcessingFailed();
    }
    public static string ConcatList(IEnumerable<string> paths) => string.Join("\n", paths.Select(path =>
    {
        string full = Path.GetFullPath(path);
        if (full.IndexOfAny(['\r', '\n', '\0']) >= 0) throw MediaMessages.ProcessingFailed();
        return "file '" + full.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    })) + "\n";
    private async Task MergeAsync(string source, string destination, MediaRequest r, CancellationToken ct)
    {
        string[] paths = new[] { source }.Concat(r.AdditionalInputs ?? []).ToArray(); if (paths.Length is < 2 or > 100) throw MediaMessages.ProcessingFailed();
        var infos = r.InputDetails?.ToArray() ?? throw MediaMessages.InspectFailed(); if (infos.Length != paths.Length) throw MediaMessages.InspectFailed();
        if (_ffmpeg.IsProbeAvailable)
        {
            var probe = new FFmpegMediaInspector(_ffmpeg);
            for (int i = 0; i < paths.Length; i++) infos[i] = await probe.InspectAsync(paths[i], ct).ConfigureAwait(false);
        }
        bool video = r.Target == MediaTargets.Mp4;
        if (infos.Any(i => i.HasVideo != video || !video && !i.HasAudio)) throw new FileProcessingException(SR.GetString("MediaMixedMerge"));
        bool copy = infos[0].CopySignature is not null && infos.All(i => i.CopySignature == infos[0].CopySignature) &&
            (!video || infos[0].VideoCodec is "h264" or "hevc") && (!infos[0].HasAudio || infos[0].AudioCodec == "aac");
        var args = BaseArguments();
        if (copy)
        {
            using var temp = new MediaTempDirectory(); string list = Path.Combine(temp.Path, "inputs.ffconcat");
            await File.WriteAllTextAsync(list, ConcatList(paths), new UTF8Encoding(false), ct).ConfigureAwait(false);
            args.AddRange(["-protocol_whitelist", "file", "-f", "concat", "-safe", "0", "-i", list]);
            args.AddRange(video ? ["-map", "0:V:0", "-map", "0:a:0?"] : ["-map", "0:a:0", "-vn"]); args.AddRange(["-c", "copy", destination]);
            await RunAsync(args, ct).ConfigureAwait(false); return;
        }
        foreach (var path in paths) Input(args, path);
        bool audio = !video || infos.Any(i => i.HasAudio); var filters = new List<string>(); var concat = new StringBuilder();
        int width = (infos[0].Width ?? 2) / 2 * 2, height = (infos[0].Height ?? 2) / 2 * 2;
        string fps = MediaTechnical.Number(infos[0].FrameRate ?? 30);
        for (int i = 0; i < paths.Length; i++)
        {
            if (video) { filters.Add($"[{i}:V:0]scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps},setpts=PTS-STARTPTS[v{i}]"); concat.Append($"[v{i}]"); }
            if (audio)
            {
                string duration = MediaTime.Seconds(infos[i].Duration);
                filters.Add(infos[i].HasAudio ? $"[{i}:a:0]aresample=48000,aformat=channel_layouts=stereo,asetpts=PTS-STARTPTS,apad,atrim=duration={duration}[a{i}]" : $"anullsrc=r=48000:cl=stereo,atrim=duration={duration},asetpts=PTS-STARTPTS[a{i}]"); concat.Append($"[a{i}]");
            }
        }
        filters.Add(concat + $"concat=n={paths.Length}:v={(video ? 1 : 0)}:a={(audio ? 1 : 0)}" + (video ? "[vout]" : "") + (audio ? "[aout]" : ""));
        args.AddRange(["-filter_complex", string.Join(';', filters)]);
        if (video) args.AddRange(["-map", "[vout]", "-c:v", "libx264", "-pix_fmt", "yuv420p"]);
        if (audio) args.AddRange(["-map", "[aout]", "-c:a", "aac", "-b:a", "192k"]); else args.Add("-an");
        args.Add(destination); await RunAsync(args, ct).ConfigureAwait(false);
    }
}

internal sealed class MediaTempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "powertrove-media-" + Guid.NewGuid().ToString("N"));
    public MediaTempDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
