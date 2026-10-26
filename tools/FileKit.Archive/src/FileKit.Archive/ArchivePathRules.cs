namespace FileKit.Archive;

public static class ArchivePathRules
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string NormalizeEntryName(string name, ZipExtractionLimits limits)
    {
        if (name.Length > limits.MaxPathLength)
            throw new UnsafeArchiveEntryException("An entry path is too long.");
        if (name.IndexOf('\0') >= 0)
            throw new UnsafeArchiveEntryException("An entry name contains invalid characters.");

        string unified = name.Replace('\\', '/');
        if (unified.StartsWith('/') || (unified.Length >= 2 && unified[1] == ':'))
            throw new UnsafeArchiveEntryException("An entry uses an absolute path.");

        var segments = unified.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > limits.MaxPathDepth)
            throw new UnsafeArchiveEntryException("An entry is nested too deeply.");

        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        foreach (var segment in segments)
        {
            if (segment == "." || segment == "..")
                throw new UnsafeArchiveEntryException("An entry contains a relative path segment.");
            if (segment.Any(c => invalid.Contains(c) || char.IsControl(c)))
                throw new UnsafeArchiveEntryException("An entry name contains invalid characters.");
            if (segment.EndsWith('.') || segment.EndsWith(' '))
                throw new UnsafeArchiveEntryException("An entry name ends with a dot or space.");
            string stem = segment.Split('.')[0];
            if (ReservedNames.Contains(stem))
                throw new UnsafeArchiveEntryException("An entry uses a reserved device name.");
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

}
