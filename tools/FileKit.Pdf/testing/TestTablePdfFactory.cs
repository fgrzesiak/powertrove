using System.Globalization;
using System.Text;
using FileKit.Pdf.Tests;

namespace FileKit.Pdf.Testing;

internal static class TestTablePdfFactory
{
    internal static byte[] Create(bool ruled = true, int pages = 1, bool twoTables = false, bool ambiguous = false, bool noTable = false)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [" + string.Join(' ', Enumerable.Range(0, pages).Select(p => (4 + p * 2).ToString(CultureInfo.InvariantCulture) + " 0 R")) + "] /Count " + pages.ToString(CultureInfo.InvariantCulture) + " /MediaBox [0 0 595 842] >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>" };
        for (int page = 0; page < pages; page++)
        {
            var content = new StringBuilder(); Text("Report", 40, 760);
            if (!noTable) { Table(700); if (twoTables) Table(400); }
            else Text("Ordinary paragraph without a table.", 40, 700);
            objects.Add("<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 3 0 R >> >> /Contents " + (5 + page * 2).ToString(CultureInfo.InvariantCulture) + " 0 R >>");
            objects.Add(TestPdfFactory.StreamObject("", content.ToString(), compress: true));
            void Text(string value, int x, int y) => content.Append(FormattableString.Invariant($"BT /F1 10 Tf 1 0 0 1 {x} {y} Tm ({value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal)}) Tj ET\n"));
            void Table(int top)
            {
                if (ruled)
                {
                    for (int r = 0; r <= 3; r++) content.Append(FormattableString.Invariant($"40 {top - r * 30} m 400 {top - r * 30} l S\n"));
                    for (int c = 0; c <= 3; c++) content.Append(FormattableString.Invariant($"{40 + c * 120} {top - 90} m {40 + c * 120} {top} l S\n"));
                }
                string[][] rows = [["Item", "Code", "Amount"], ["Coffee beans", "00123", "12.50"], ["Tea", "=2+2", "9.00"]];
                for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) Text(rows[r][c], 45 + c * 120 + (ambiguous && r == 2 && c == 1 ? 30 : 0), top - 15 - r * 30);
            }
        }
        return TestPdfFactory.Serialize(objects, "/Root 1 0 R");
    }
}
