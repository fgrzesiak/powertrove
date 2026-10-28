using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace PowerTrove.Testing.Windows;

// Real codec and playback acceptance needs capabilities absent on some server images.
// Discovery reports an explicit skip; it never turns an unexecuted check into a pass.
internal sealed class RequiresAudioRenderTheoryAttribute : TheoryAttribute
{
    public RequiresAudioRenderTheoryAttribute()
    {
        if (!MediaTestCapabilities.Check(async token =>
            (await DeviceInformation.FindAllAsync(DeviceClass.AudioRender).AsTask(token)).Any(device => device.IsEnabled)))
            Skip = "Requires an enabled Windows audio render device; unavailable in this environment.";
    }
}

internal sealed class RequiresHeifEncoderFactAttribute : FactAttribute
{
    public RequiresHeifEncoderFactAttribute()
    {
        if (!MediaTestCapabilities.Check(async token =>
        {
            var codec = BitmapEncoder.GetEncoderInformationEnumerator().FirstOrDefault(info => info.FileExtensions.Contains(".heic"));
            if (codec is null || !BitmapDecoder.GetDecoderInformationEnumerator().Any(info => info.FileExtensions.Contains(".heic"))) return false;
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(codec.CodecId, stream).AsTask(token);
            var pixels = new byte[32 * 24 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            { pixels[i] = 40; pixels[i + 1] = 100; pixels[i + 2] = 200; pixels[i + 3] = 255; }
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 32, 24, 96, 96, pixels);
            await encoder.FlushAsync().AsTask(token);
            return stream.Size > 0;
        }))
            Skip = "Requires a usable optional Windows HEIF encoder and decoder; unavailable in this environment.";
    }
}

internal static class MediaTestCapabilities
{
    public static bool Check(Func<CancellationToken, Task<bool>> probe)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { return Task.Run(() => probe(timeout.Token)).WaitAsync(TimeSpan.FromSeconds(6)).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is COMException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        { return false; }
    }
}
