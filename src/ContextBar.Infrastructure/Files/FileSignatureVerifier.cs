using System;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Abstractions.Files;
using FileKit.Detection;
using ContextBar.Abstractions.Office;

namespace ContextBar.Infrastructure.Files;

/// <summary>
/// Production <see cref="IFileTypeVerifier"/>: compares the content signature
/// (FileKit.Detection) with the formats an action accepts. Reads at most 1 KB.
/// </summary>
public sealed class FileSignatureVerifier : IFileTypeVerifier
{
    public async ValueTask<FileTypeVerification> VerifyAsync(string path, FileInputFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (OfficeFormats.ApplicationFor(path) is OfficeApplication application && filter.AcceptsFormat(OfficeFormats.FormatFor(application)))
        {
            try
            {
                await new OfficeInputValidator().ValidateAsync(path, application, false, cancellationToken).ConfigureAwait(false);
                return FileTypeVerification.Accepted(OfficeFormats.FormatFor(application));
            }
            catch (OfficeConversionException ex) { return FileTypeVerification.Rejected(null, OfficeErrors.Message(ex.Failure)); }
            catch (UnauthorizedAccessException) { return FileTypeVerification.Rejected(null, OfficeErrors.Message(OfficeFailure.InvalidDocument)); }
            catch (System.IO.IOException) { return FileTypeVerification.Rejected(null, OfficeErrors.Message(OfficeFailure.InvalidDocument)); }
        }
        DetectionResult detected;
        try
        {
            detected = await FileSignature.DetectFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            return FileTypeVerification.Rejected(null, ContextBar.Infrastructure.Resources.Strings.FileAccessDenied);
        }
        catch (System.IO.IOException)
        {
            return FileTypeVerification.Rejected(null, ContextBar.Infrastructure.Resources.Strings.FileReadFailed);
        }

        string? format = Map(detected);
        if (filter.AcceptsFormat(format))
            return FileTypeVerification.Accepted(format);

        return FileTypeVerification.Rejected(format, format is null
            ? ContextBar.Infrastructure.Resources.Strings.FileFormatNotRecognized(filter.DisplayName)
            : ContextBar.Infrastructure.Resources.Strings.FileFormatMismatch(filter.DisplayName));
    }

    /// <summary>Maps a detection result to the stable <see cref="FileFormats"/> identifiers.</summary>
    public static string? Map(DetectionResult result) => result.Format switch
    {
        FileFormat.Jpeg => FileFormats.Jpeg,
        FileFormat.Png => FileFormats.Png,
        FileFormat.Gif => FileFormats.Gif,
        FileFormat.Bmp => FileFormats.Bmp,
        FileFormat.Tiff => FileFormats.Tiff,
        FileFormat.WebP => FileFormats.WebP,
        FileFormat.Avif => FileFormats.Avif,
        FileFormat.Heic => FileFormats.Heic,
        FileFormat.Ico => FileFormats.Ico,
        FileFormat.Pdf => FileFormats.Pdf,
        FileFormat.Zip => FileFormats.Zip,
        FileFormat.Tar => FileFormats.Tar,
        FileFormat.Gzip => FileFormats.Gzip,
        FileFormat.Mp3 => FileFormats.Mp3,
        FileFormat.Aac => FileFormats.Aac,
        FileFormat.Wav => FileFormats.Wav,
        FileFormat.Flac => FileFormats.Flac,
        FileFormat.Ogg => FileFormats.Ogg,
        FileFormat.IsoMedia => FileFormats.IsoMedia,
        FileFormat.Matroska => FileFormats.Matroska,
        FileFormat.Avi => FileFormats.Avi,
        FileFormat.Asf => FileFormats.Asf,
        _ => null
    };
}
