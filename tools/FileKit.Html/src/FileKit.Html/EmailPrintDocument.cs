namespace FileKit.Html;

public sealed class EmailPrintException(string message, Exception? inner = null) : Exception(message, inner);
public sealed record EmailPrintLabels(string From, string To, string Cc, string Subject, string Date, string Attachments, string Notice);
public sealed record EmailAttachment(string FileName, ReadOnlyMemory<byte> Data);
public sealed record EmailPrintDocument(string Html, int AttachmentCount, IReadOnlyList<EmailAttachment> Attachments);
public interface IEmailPrintReader
{
    Task<EmailPrintDocument> ReadAsync(Stream source, EmailPrintLabels labels, bool exportAttachments, CancellationToken cancellationToken);
}

public static class EmailAttachmentNames
{
    /// <summary>Portable leaf names, prefixed by sequence to prevent collisions and reserved-device names.</summary>
    public static string ForExport(string name, int index)
    {
        string leaf = name.Replace('\\', '/').Split('/')[^1];
        leaf = new string(leaf.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format && !"<>:\"/\\|?*".Contains(c)).ToArray()).Trim(' ', '.');
        if (leaf.Length == 0) leaf = "attachment.bin";
        if (leaf.Length > 120) leaf = leaf[..100] + Path.GetExtension(leaf)[..Math.Min(20, Path.GetExtension(leaf).Length)];
        return index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + "-" + leaf;
    }
}
