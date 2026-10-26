using System;

namespace ContextBar.Abstractions;

public static class SelectionFamilies
{
    public static string FromExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return "other";

        return extension.ToLowerInvariant() switch
        {
            ".pdf" => "pdf",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" or ".avif" or ".heic" or ".heif" => "image",
            ".mp3" or ".wav" or ".aac" or ".ogg" or ".flac" or ".m4a" or ".wma" => "audio",
            ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" or ".m4v" => "video",
            ".srt" or ".vtt" or ".ass" or ".ssa" => "subtitle",
            ".zip" or ".tar" or ".gz" or ".tgz" or ".7z" or ".rar" => "archive",
            ".doc" or ".docx" or ".odt" or ".rtf" or ".ppt" or ".pptx" or ".odp" => "document",
            ".xls" or ".xlsx" or ".ods" or ".csv" or ".tsv" => "spreadsheet",
            ".txt" or ".md" or ".json" or ".xml" or ".log" or ".ini" or ".yaml" or ".yml" => "text",
            _ => "other"
        };
    }

    public static string Of(SelectedFile file)
    {
        if (file.IsDirectory)
        {
            return "folder";
        }

        return FromExtension(file.Extension);
    }
}
