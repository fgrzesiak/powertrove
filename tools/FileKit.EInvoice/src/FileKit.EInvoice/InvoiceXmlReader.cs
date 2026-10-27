using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace FileKit.EInvoice;

/// <summary>Selected-field projection only; performs no schema/business-rule conformance validation.</summary>
public static class InvoiceXmlReader
{
    public const int MaxBytes = 5 * 1024 * 1024;
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Rsm = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";
    private static readonly XNamespace Ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
    private static readonly XNamespace Udt = "urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100";
    public static async Task<Invoice> ReadAsync(Stream input, CancellationToken ct = default)
    {
        using var bytes = new MemoryStream(); byte[] buffer = new byte[81920]; int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + read > MaxBytes) throw new InvoiceException("Invoice XML exceeds 5 MiB.");
            await bytes.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return await Task.Run(() => Parse(bytes.ToArray(), ct), ct).ConfigureAwait(false);
    }
    public static Invoice Parse(byte[] bytes, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested(); if (bytes.Length > MaxBytes) throw new InvoiceException("Invoice XML exceeds 5 MiB.");
            using var stream = new MemoryStream(bytes); var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes };
            using (var bounded = XmlReader.Create(stream, settings))
            {
                int count = 0;
                while (bounded.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (bounded.Depth > 64 || bounded.NodeType == XmlNodeType.Element && ++count > 100000 || bounded.HasValue && bounded.Value.Length > 32767) throw new InvoiceException("Invoice XML structure/text limit exceeded.");
                }
            }
            stream.Position = 0; using var reader = XmlReader.Create(stream, settings); var root = XDocument.Load(reader).Root ?? throw new InvoiceException("Missing invoice root.");
            Invoice result = root.Name.NamespaceName switch
            {
                "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2" when root.Name.LocalName == "Invoice" => Ubl(root, false, ct),
                "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2" when root.Name.LocalName == "CreditNote" => Ubl(root, true, ct),
                "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100" when root.Name.LocalName == "CrossIndustryInvoice" => Cii(root, ct),
                _ => throw new InvoiceException("Unsupported invoice XML syntax.")
            };
            if (result.Lines.Count is 0 or > 10000 || result.Taxes.Count > 100 || result.Payments.Count > 100 || result.Currency.Length != 3 || result.Currency.Any(c => c is < 'A' or > 'Z')) throw new InvoiceException("Unsupported invoice shape or currency notation.");
            return result;
        }
        catch (Exception ex) when (ex is XmlException or FormatException or OverflowException or ArgumentException)
        { throw new InvoiceException("Invalid invoice XML, number or date.", ex); }
    }
    private static Invoice Ubl(XElement root, bool credit, CancellationToken ct)
    {
        string currency = Required(root, Cbc + "DocumentCurrencyCode"); var total = One(root, Cac + "LegalMonetaryTotal", true)!;
        var taxTotals = root.Elements(Cac + "TaxTotal").Where(t => (string?)One(t, Cbc + "TaxAmount")?.Attribute("currencyID") is not string c || c == currency).ToArray();
        if (taxTotals.Length != 1) throw new InvoiceException("A single invoice-currency tax total is required.");
        var taxes = taxTotals[0].Elements(Cac + "TaxSubtotal").Select(t =>
        {
            var category = One(t, Cac + "TaxCategory", true)!;
            return new InvoiceTax(Text(category, Cbc + "ID"), Number(One(category, Cbc + "Percent")), Amount(t, Cbc + "TaxableAmount", currency), Amount(t, Cbc + "TaxAmount", currency), Text(category, Cbc + "TaxExemptionReason"));
        }).ToArray();
        var lines = new List<InvoiceLine>();
        foreach (var line in root.Elements(Cac + (credit ? "CreditNoteLine" : "InvoiceLine")))
        {
            ct.ThrowIfCancellationRequested(); if (lines.Count >= 10000) throw new InvoiceException("Invoice line limit exceeded.");
            var item = One(line, Cac + "Item", true)!; var price = One(line, Cac + "Price"); var tax = One(item, Cac + "ClassifiedTaxCategory"); var quantity = One(line, Cbc + (credit ? "CreditedQuantity" : "InvoicedQuantity"), true)!;
            lines.Add(new(Required(line, Cbc + "ID"), Join(Text(item, Cbc + "Name"), Text(item, Cbc + "Description"), Text(line, Cbc + "Note")), Number(quantity)!.Value, (string?)quantity.Attribute("unitCode") ?? "", OptionalAmount(price, Cbc + "PriceAmount", currency), Number(One(price, Cbc + "BaseQuantity")), Amount(line, Cbc + "LineExtensionAmount", currency), Text(tax, Cbc + "ID"), Number(One(tax, Cbc + "Percent"))));
        }
        var totals = new InvoiceTotals(OptionalAmount(total, Cbc + "LineExtensionAmount", currency), OptionalAmount(total, Cbc + "AllowanceTotalAmount", currency), OptionalAmount(total, Cbc + "ChargeTotalAmount", currency), Amount(total, Cbc + "TaxExclusiveAmount", currency), Amount(taxTotals[0], Cbc + "TaxAmount", currency), Amount(total, Cbc + "TaxInclusiveAmount", currency), OptionalAmount(total, Cbc + "PrepaidAmount", currency), OptionalAmount(total, Cbc + "PayableRoundingAmount", currency), Amount(total, Cbc + "PayableAmount", currency));
        string terms = Join(root.Elements(Cac + "PaymentTerms").Select(t => Text(t, Cbc + "Note")).ToArray());
        var payments = root.Elements(Cac + "PaymentMeans").Select(p => new InvoicePayment(Text(p, Cbc + "PaymentMeansCode"), Text(One(p, Cac + "PayeeFinancialAccount"), Cbc + "ID"), Text(p, Cbc + "PaymentID"), terms)).ToArray();
        return new("UBL", Required(root, Cbc + "CustomizationID"), Required(root, Cbc + "ID"), Date(Required(root, Cbc + "IssueDate"), "yyyy-MM-dd"), Required(root, Cbc + (credit ? "CreditNoteTypeCode" : "InvoiceTypeCode")), currency, Text(root, Cbc + "BuyerReference"), UblParty(One(root, Cac + "AccountingSupplierParty", true)!), UblParty(One(root, Cac + "AccountingCustomerParty", true)!), lines.AsReadOnly(), Array.AsReadOnly(taxes), totals, Array.AsReadOnly(payments), Join(root.Elements(Cbc + "Note").Select(n => n.Value.Trim()).ToArray()));
    }
    private static InvoiceParty UblParty(XElement wrapper)
    {
        var party = One(wrapper, Cac + "Party", true)!; var legal = One(party, Cac + "PartyLegalEntity"); var address = One(party, Cac + "PostalAddress");
        return new(RequiredText(Text(legal, Cbc + "RegistrationName"), Text(One(party, Cac + "PartyName"), Cbc + "Name")), Join(Text(address, Cbc + "StreetName"), Text(address, Cbc + "AdditionalStreetName"), Text(address, Cbc + "PostalZone"), Text(address, Cbc + "CityName"), Text(One(address, Cac + "Country"), Cbc + "IdentificationCode")), Join(party.Elements(Cac + "PartyTaxScheme").Select(t => Text(t, Cbc + "CompanyID")).ToArray()), Text(legal, Cbc + "CompanyID"), Text(One(party, Cac + "Contact"), Cbc + "ElectronicMail"));
    }
    private static Invoice Cii(XElement root, CancellationToken ct)
    {
        var document = One(root, Rsm + "ExchangedDocument", true)!; var transaction = One(root, Rsm + "SupplyChainTradeTransaction", true)!;
        var agreement = One(transaction, Ram + "ApplicableHeaderTradeAgreement", true)!; var settlement = One(transaction, Ram + "ApplicableHeaderTradeSettlement", true)!; var total = One(settlement, Ram + "SpecifiedTradeSettlementHeaderMonetarySummation", true)!;
        string currency = Required(settlement, Ram + "InvoiceCurrencyCode"); var date = One(One(document, Ram + "IssueDateTime", true), Udt + "DateTimeString", true)!;
        if ((string?)date.Attribute("format") != "102") throw new InvoiceException("Unsupported CII date format.");
        var lines = new List<InvoiceLine>();
        foreach (var line in transaction.Elements(Ram + "IncludedSupplyChainTradeLineItem"))
        {
            ct.ThrowIfCancellationRequested(); if (lines.Count >= 10000) throw new InvoiceException("Invoice line limit exceeded.");
            var product = One(line, Ram + "SpecifiedTradeProduct", true)!; var lineAgreement = One(line, Ram + "SpecifiedLineTradeAgreement"); var price = One(lineAgreement, Ram + "NetPriceProductTradePrice"); var lineSettlement = One(line, Ram + "SpecifiedLineTradeSettlement", true)!; var tax = One(lineSettlement, Ram + "ApplicableTradeTax"); var quantity = One(One(line, Ram + "SpecifiedLineTradeDelivery", true), Ram + "BilledQuantity", true)!;
            var lineDocument = One(line, Ram + "AssociatedDocumentLineDocument", true)!;
            lines.Add(new(Required(lineDocument, Ram + "LineID"), Join(Text(product, Ram + "Name"), Text(product, Ram + "Description")), Number(quantity)!.Value, (string?)quantity.Attribute("unitCode") ?? "", OptionalAmount(price, Ram + "ChargeAmount", currency), Number(One(price, Ram + "BasisQuantity")), Amount(One(lineSettlement, Ram + "SpecifiedTradeSettlementLineMonetarySummation", true), Ram + "LineTotalAmount", currency), Text(tax, Ram + "CategoryCode"), Number(One(tax, Ram + "RateApplicablePercent"))));
        }
        var taxes = settlement.Elements(Ram + "ApplicableTradeTax").Select(t => new InvoiceTax(Text(t, Ram + "CategoryCode"), Number(One(t, Ram + "RateApplicablePercent")), Amount(t, Ram + "BasisAmount", currency), Amount(t, Ram + "CalculatedAmount", currency), Text(t, Ram + "ExemptionReason"))).ToArray();
        var totals = new InvoiceTotals(OptionalAmount(total, Ram + "LineTotalAmount", currency), OptionalAmount(total, Ram + "AllowanceTotalAmount", currency), OptionalAmount(total, Ram + "ChargeTotalAmount", currency), Amount(total, Ram + "TaxBasisTotalAmount", currency), Amount(total, Ram + "TaxTotalAmount", currency), Amount(total, Ram + "GrandTotalAmount", currency), OptionalAmount(total, Ram + "TotalPrepaidAmount", currency), OptionalAmount(total, Ram + "RoundingAmount", currency), Amount(total, Ram + "DuePayableAmount", currency));
        string terms = Join(settlement.Elements(Ram + "SpecifiedTradePaymentTerms").Select(t => Text(t, Ram + "Description")).ToArray());
        var payments = settlement.Elements(Ram + "SpecifiedTradeSettlementPaymentMeans").Select(p => new InvoicePayment(Text(p, Ram + "TypeCode"), Text(One(p, Ram + "PayeePartyCreditorFinancialAccount"), Ram + "IBANID"), Text(settlement, Ram + "PaymentReference"), terms)).ToArray();
        string profile = Required(One(One(root, Rsm + "ExchangedDocumentContext", true), Ram + "GuidelineSpecifiedDocumentContextParameter", true), Ram + "ID");
        return new("CII", profile, Required(document, Ram + "ID"), Date(date.Value.Trim(), "yyyyMMdd"), Required(document, Ram + "TypeCode"), currency, Text(agreement, Ram + "BuyerReference"), CiiParty(One(agreement, Ram + "SellerTradeParty", true)!), CiiParty(One(agreement, Ram + "BuyerTradeParty", true)!), lines.AsReadOnly(), Array.AsReadOnly(taxes), totals, Array.AsReadOnly(payments), Join(document.Elements(Ram + "IncludedNote").Select(n => Text(n, Ram + "Content")).ToArray()));
    }
    private static InvoiceParty CiiParty(XElement party)
    {
        var address = One(party, Ram + "PostalTradeAddress");
        return new(Required(party, Ram + "Name"), Join(Text(address, Ram + "LineOne"), Text(address, Ram + "LineTwo"), Text(address, Ram + "LineThree"), Text(address, Ram + "PostcodeCode"), Text(address, Ram + "CityName"), Text(address, Ram + "CountryID")), Join(party.Elements(Ram + "SpecifiedTaxRegistration").Select(t => Text(t, Ram + "ID")).ToArray()), Text(One(party, Ram + "SpecifiedLegalOrganization"), Ram + "ID"), Text(One(One(party, Ram + "DefinedTradeContact"), Ram + "EmailURIUniversalCommunication"), Ram + "URIID"));
    }
    private static XElement? One(XElement? parent, XName name, bool required = false)
    {
        var items = parent?.Elements(name).Take(2).ToArray() ?? [];
        if (items.Length > 1 || required && items.Length == 0) throw new InvoiceException("Missing or ambiguous invoice field."); return items.FirstOrDefault();
    }
    private static string Text(XElement? parent, XName name) => One(parent, name)?.Value.Trim() ?? "";
    private static string Required(XElement? parent, XName name) => RequiredText(Text(parent, name));
    private static string RequiredText(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? throw new InvoiceException("A required invoice field is missing.");
    private static decimal? Number(XElement? element)
    {
        if (element is null) return null;
        string source = element.Value.Trim();
        if (!decimal.TryParse(source, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value) || Canonical(source) != Canonical(value.ToString("0.############################", CultureInfo.InvariantCulture))) throw new InvoiceException("Invalid or precision-sensitive invoice decimal value."); return value;
    }
    private static string Canonical(string source)
    {
        bool negative = source.StartsWith('-'); string unsigned = source.TrimStart('+', '-');
        var parts = unsigned.Split('.'); if (parts.Length > 2 || parts.Any(p => p.Any(c => c is < '0' or > '9'))) throw new InvoiceException("Invalid invoice decimal notation.");
        string whole = parts[0].TrimStart('0'); if (whole.Length == 0) whole = "0";
        string fraction = parts.Length == 2 ? parts[1].TrimEnd('0') : "";
        return (negative && (whole != "0" || fraction.Length > 0) ? "-" : "") + whole + (fraction.Length > 0 ? "." + fraction : "");
    }
    private static decimal Amount(XElement? parent, XName name, string currency) => OptionalAmount(parent, name, currency) ?? throw new InvoiceException("A required invoice amount is missing.");
    private static decimal? OptionalAmount(XElement? parent, XName name, string currency)
    {
        var element = One(parent, name); if (element is not null && (string?)element.Attribute("currencyID") is string code && code != currency) throw new InvoiceException("Mixed-currency amounts are unsupported."); return Number(element);
    }
    private static DateOnly Date(string value, string format) => DateOnly.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw new InvoiceException("Invalid invoice date.");
    private static string Join(params string[] values) => string.Join("\n", values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal));
}
