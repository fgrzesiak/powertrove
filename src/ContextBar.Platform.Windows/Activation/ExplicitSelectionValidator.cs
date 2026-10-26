using ContextBar.Core.Activation;

namespace ContextBar.Platform.Windows.Activation;

public interface IActivationFileProbe
{
    SelectedFile? ReadLocalFile(string path);
}

public sealed class ExplicitSelectionValidator(IActivationFileProbe? probe = null)
{
    private readonly IActivationFileProbe _probe = probe ?? new LocalActivationFileProbe();

    public ContextSnapshot? CreateSnapshot(ExplicitSelectionMessage message, CancellationToken token = default)
    {
        if (message.FilePaths is not { Count: > 0 and <= ActivationProtocol.MaxFiles }
            || !ActivationProtocol.IsActionId(message.ActionId)) return null;
        var files = new List<SelectedFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in message.FilePaths)
        {
            token.ThrowIfCancellationRequested();
            if (!ActivationProtocol.IsLocalPath(path)) return null;
            try
            {
                var file = _probe.ReadLocalFile(path);
                // Reject the whole request; never turn an invalid multi-selection into a partial one.
                if (file is null) return null;
                if (seen.Add(path)) files.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { return null; }
        }
        var now = DateTimeOffset.UtcNow;
        return new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 0, null, now),
            ContextStatus.Available, new FileSelection(files.AsReadOnly()), now, now.AddMinutes(5), "explicit-selection", null);
    }
}

internal sealed class LocalActivationFileProbe : IActivationFileProbe
{
    public SelectedFile? ReadLocalFile(string path)
    {
        if (!string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase)) return null;
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram)) return null;
        // Do not follow junctions/symlinks or hydrate cloud placeholders, including in parent directories.
        const FileAttributes unavailable = FileAttributes.ReparsePoint | FileAttributes.Offline
            | (FileAttributes)0x40000 | (FileAttributes)0x400000;
        for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & unavailable) != 0) return null;
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0) return null;
        return new SelectedFile(path, info.Name, info.Extension.ToLowerInvariant(), null, info.Length, true, true, null);
    }
}
