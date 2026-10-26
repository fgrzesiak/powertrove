using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FileKit.Tabular;

public static partial class MarkdownTableWriter
{
    public static string EscapeMarkdownField(string field)
    {
        if (string.IsNullOrEmpty(field)) return "";

        // Escape pipes and newlines
        string escaped = field.Replace("|", "&#124;");
        escaped = escaped.Replace("\r\n", "<br>");
        escaped = escaped.Replace("\n", "<br>");
        escaped = escaped.Replace("\r", "<br>");

        return escaped;
    }
}
