using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PowerTrove.Abstractions.Office;

public enum OfficeApplication { Word, Excel, PowerPoint }

public enum OfficeFailure
{
    MissingApplication, PasswordProtected, UnsafeMacros, InvalidDocument,
    Busy, TimedOut, ConversionFailed, PreferenceRestoreFailed
}

/// <summary>Technical failure code only. Presentation layers supply localized, content-free text.</summary>
public sealed class OfficeConversionException : Exception
{
    public OfficeConversionException(OfficeFailure failure) : base(failure.ToString()) => Failure = failure;
    public OfficeFailure Failure { get; }
}

/// <summary>Shared conversion port; PDF and Office packs never reference each other.</summary>
public interface IOfficeConverter
{
    bool IsAvailable(OfficeApplication application);
    Task ConvertToPdfAsync(string sourcePath, string destinationPdfPath, CancellationToken cancellationToken);
    Task ConvertPdfToWordAsync(string sourcePath, string destinationDocxPath, CancellationToken cancellationToken);
}

/// <summary>Preflight must run on the private input copy, before Office opens any content.</summary>
public interface IOfficeInputValidator
{
    Task ValidateAsync(string path, OfficeApplication application, bool pdfToWord, CancellationToken cancellationToken);
}

public static class OfficeFormats
{
    public const string Word = "office-word";
    public const string Excel = "office-excel";
    public const string PowerPoint = "office-powerpoint";
    public static IReadOnlyList<string> WordExtensions { get; } = Array.AsReadOnly(new[] { ".doc", ".docx", ".odt", ".rtf" });
    public static IReadOnlyList<string> ExcelExtensions { get; } = Array.AsReadOnly(new[] { ".xls", ".xlsx", ".ods", ".csv" });
    public static IReadOnlyList<string> PowerPointExtensions { get; } = Array.AsReadOnly(new[] { ".ppt", ".pptx", ".odp" });

    public static OfficeApplication? ApplicationFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".doc" or ".docx" or ".odt" or ".rtf" => OfficeApplication.Word,
        ".xls" or ".xlsx" or ".ods" or ".csv" => OfficeApplication.Excel,
        ".ppt" or ".pptx" or ".odp" => OfficeApplication.PowerPoint,
        _ => null
    };

    public static string FormatFor(OfficeApplication application) => application switch
    {
        OfficeApplication.Word => Word,
        OfficeApplication.Excel => Excel,
        OfficeApplication.PowerPoint => PowerPoint,
        _ => throw new ArgumentOutOfRangeException(nameof(application))
    };
}
