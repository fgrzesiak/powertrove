using System.Globalization;
using System.Net;
using System.Text;
using FileKit.EInvoice;
using X = FileKit.Xlsx;
using T = FileKit.Tabular;

namespace PowerTrove.Pack.EInvoice;

internal static class InvoicePresentation
{
    private static readonly string[] LineKeys = ["InvoiceId", "IssueDate", "Seller", "Buyer", "Currency", "BuyerReference", "LineId", "DescriptionColumn", "Quantity", "Unit", "UnitPrice", "PriceBase", "LineNet", "TaxCategory", "TaxRate", "Net", "Tax", "Gross", "Payable"];
    public static string Number(decimal? value) => value?.ToString("0.############################", CultureInfo.InvariantCulture) ?? "";
    public static IReadOnlyList<IReadOnlyList<string>> Lines(Invoice invoice) => invoice.Lines.Select(line => (IReadOnlyList<string>)new[] { invoice.Id, invoice.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), invoice.Seller.Name, invoice.Buyer.Name, invoice.Currency, invoice.BuyerReference, line.Id, line.Description, Number(line.Quantity), line.Unit, Number(line.UnitPrice), Number(line.PriceBaseQuantity), Number(line.NetAmount), line.TaxCategory, Number(line.TaxRate), Number(invoice.Totals.Net), Number(invoice.Totals.Tax), Number(invoice.Totals.Gross), Number(invoice.Totals.Payable) }).ToArray();
    private static IReadOnlyList<(string Key, string Value)> Header(Invoice i) => new (string, string)[] {
        ("InvoiceId", i.Id), ("IssueDate", i.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("Syntax", i.Syntax), ("Profile", i.Profile), ("TypeCode", i.TypeCode), ("Currency", i.Currency), ("BuyerReference", i.BuyerReference),
        ("Seller", i.Seller.Name), ("SellerAddress", i.Seller.Address), ("SellerTaxId", i.Seller.TaxId), ("SellerRegistration", i.Seller.RegistrationId), ("SellerEmail", i.Seller.Email),
        ("Buyer", i.Buyer.Name), ("BuyerAddress", i.Buyer.Address), ("BuyerTaxId", i.Buyer.TaxId), ("BuyerRegistration", i.Buyer.RegistrationId), ("BuyerEmail", i.Buyer.Email),
        ("LineNet", Number(i.Totals.LineNet)), ("Allowances", Number(i.Totals.Allowances)), ("Charges", Number(i.Totals.Charges)), ("Net", Number(i.Totals.Net)), ("Tax", Number(i.Totals.Tax)), ("Gross", Number(i.Totals.Gross)), ("Prepaid", Number(i.Totals.Prepaid)), ("Rounding", Number(i.Totals.Rounding)), ("Payable", Number(i.Totals.Payable)), ("Notes", i.Notes) };
    private static readonly string[] TaxKeys = ["TaxCategory", "TaxRate", "Taxable", "Tax", "Exemption"];
    private static IReadOnlyList<IReadOnlyList<string>> Taxes(Invoice invoice) => invoice.Taxes.Select(t => (IReadOnlyList<string>)new[] { t.Category, Number(t.Rate), Number(t.TaxableAmount), Number(t.TaxAmount), t.ExemptionReason }).ToArray();
    private static readonly string[] PaymentKeys = ["PaymentCode", "Account", "Reference", "Terms"];
    private static IReadOnlyList<IReadOnlyList<string>> Payments(Invoice invoice) => invoice.Payments.Select(p => (IReadOnlyList<string>)new[] { p.Code, p.Account, p.Reference, p.Terms }).ToArray();
    public static string Html(Invoice invoice, CancellationToken ct, bool standalone = true)
    {
        var html = new StringBuilder("<!doctype html><html lang='").Append(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName).Append("'><head><meta charset='utf-8'>").Append(standalone ? "<meta http-equiv='Content-Security-Policy' content=\"default-src 'none';style-src 'unsafe-inline';base-uri 'none';form-action 'none'\">" : "").Append("<title>").Append(E(SR.Get("ViewTitle"))).Append("</title><style>body{font:11pt sans-serif;margin:1em}table{width:100%;border-collapse:collapse;margin-bottom:1em}td,th{border:1px solid #bbb;padding:.35em;text-align:left;vertical-align:top;white-space:pre-wrap;overflow-wrap:anywhere}thead{display:table-header-group}h2{break-after:avoid}.notice{border:2px solid #765;padding:1em}</style></head><body><h1>").Append(E(SR.Get("Visualization"))).Append("</h1><p class='notice'>").Append(E(SR.Get("Notice"))).Append("</p>");
        foreach (var field in Header(invoice)) { ct.ThrowIfCancellationRequested(); html.Append("<p><strong>").Append(E(SR.Get(field.Key))).Append(": </strong>").Append(E(field.Value)).Append("</p>"); }
        Table("Lines", LineKeys.Skip(6).Take(9).ToArray(), Lines(invoice).Select(row => (IReadOnlyList<string>)row.Skip(6).Take(9).ToArray()).ToArray()); Table("Taxes", TaxKeys, Taxes(invoice)); Table("Payments", PaymentKeys, Payments(invoice));
        html.Append("</body></html>"); return html.ToString();
        void Table(string title, string[] columns, IReadOnlyList<IReadOnlyList<string>> rows)
        {
            html.Append("<h2>").Append(E(SR.Get(title))).Append("</h2><table><thead><tr>"); foreach (string key in columns) html.Append("<th>").Append(E(SR.Get(key))).Append("</th>"); html.Append("</tr></thead><tbody>");
            foreach (var row in rows) { ct.ThrowIfCancellationRequested(); html.Append("<tr>"); foreach (string cell in row) html.Append("<td>").Append(E(cell)).Append("</td>"); html.Append("</tr>"); } html.Append("</tbody></table>");
        }
    }
    public static X.Workbook Workbook(Invoice invoice)
    {
        var sheets = new List<X.Worksheet>();
        Add("Invoice", ["Field", "Value"], Header(invoice).Select(f => (IReadOnlyList<string>)new[] { SR.Get(f.Key), f.Value }).Concat(new[] { (IReadOnlyList<string>)new[] { SR.Get("Notice"), "" } }).ToArray());
        Add("Lines", LineKeys, Lines(invoice)); Add("Taxes", TaxKeys, Taxes(invoice)); Add("Payments", PaymentKeys, Payments(invoice)); return new(sheets);
        void Add(string title, string[] keys, IReadOnlyList<IReadOnlyList<string>> rows)
        {
            var all = new[] { (IReadOnlyList<X.Cell>)keys.Select(k => (X.Cell)new X.TextCell(SR.Get(k))).ToArray() }.Concat(rows.Select(row => (IReadOnlyList<X.Cell>)row.Select(value => (X.Cell)new X.TextCell(value)).ToArray())).ToArray();
            sheets.Add(new(SR.Get(title), all, true));
        }
    }
    public static void Csv(TextWriter writer, Invoice invoice, CancellationToken ct)
    {
        var rows = Lines(invoice).Select(row => (IReadOnlyList<T.Cell>)row.Select(value => (T.Cell)new T.TextCell(SafeCsv(value))).ToArray()).ToArray(); T.CsvWriter.Write(writer, new(LineKeys.Select(SR.Get).ToArray(), rows), cancellationToken: ct);
    }
    private static string SafeCsv(string value)
    {
        string start = value.TrimStart(); if (start.Length > 0 && (start[0] is '=' or '+' or '@' || start[0] == '-' && !decimal.TryParse(start, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))) return "'" + value; return value;
    }
    private static string E(string value) => WebUtility.HtmlEncode(value);
}
