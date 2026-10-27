using System.Text;
using FileKit.EInvoice.Testing;
namespace FileKit.EInvoice.Tests;

public sealed class InvoiceXmlTests
{
    [Fact]
    public void UblAndCiiProjectEquivalentSourceValuesWithoutFloatingPointOrRecalculation()
    {
        foreach (byte[] xml in new[] { TestInvoiceFactory.Ubl(), TestInvoiceFactory.Cii() })
        {
            var invoice = InvoiceXmlReader.Parse(xml); Assert.Equal("000123", invoice.Id); Assert.Equal(new DateOnly(2026, 10, 27), invoice.IssueDate); Assert.Equal("EUR", invoice.Currency);
            Assert.Equal("Seller Grüße", invoice.Seller.Name); var line = Assert.Single(invoice.Lines); Assert.Equal(2m, line.Quantity); Assert.Equal(50m, line.UnitPrice); Assert.Equal(100m, line.NetAmount); Assert.Equal("001", line.Id);
            Assert.Equal(19m, invoice.Totals.Tax); Assert.Equal(119m, invoice.Totals.Gross); Assert.Equal(99m, invoice.Totals.Payable); Assert.Equal(19m, Assert.Single(invoice.Taxes).Rate);
        }
        var inconsistent = InvoiceXmlReader.Parse(TestInvoiceFactory.Ubl("123.45")); Assert.Equal(123.45m, inconsistent.Lines[0].NetAmount); Assert.Equal(100m, inconsistent.Totals.Net);
    }
    [Theory]
    [InlineData("1.00000000000000000000000000001")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("1,23")]
    [InlineData("1e2")]
    public void UnsupportedDecimalPrecisionAndNotationAreNeverSilentlyRounded(string value) => Assert.Throws<InvoiceException>(() => InvoiceXmlReader.Parse(TestInvoiceFactory.Ubl(value)));
    [Fact]
    public void UblCreditNotesRetainTheirDeclaredTypeAndSourceSigns()
    {
        string xml = Encoding.UTF8.GetString(TestInvoiceFactory.Ubl()).Replace("<Invoice ", "<CreditNote ", StringComparison.Ordinal).Replace("</Invoice>", "</CreditNote>", StringComparison.Ordinal)
            .Replace("xsd:Invoice-2", "xsd:CreditNote-2", StringComparison.Ordinal).Replace("InvoiceTypeCode", "CreditNoteTypeCode", StringComparison.Ordinal).Replace(">380<", ">381<", StringComparison.Ordinal)
            .Replace("InvoiceLine", "CreditNoteLine", StringComparison.Ordinal).Replace("InvoicedQuantity", "CreditedQuantity", StringComparison.Ordinal);
        var invoice = InvoiceXmlReader.Parse(Encoding.UTF8.GetBytes(xml)); Assert.Equal("381", invoice.TypeCode); Assert.Equal(2m, invoice.Lines[0].Quantity); Assert.Equal(99m, invoice.Totals.Payable);
    }
    [Fact]
    public async Task XmlEntitiesDepthSizeAmbiguityAndCancellationAreBounded()
    {
        Assert.Throws<InvoiceException>(() => InvoiceXmlReader.Parse(Encoding.UTF8.GetBytes("<!DOCTYPE invoice [<!ENTITY x SYSTEM 'file:///private'>]><invoice>&x;</invoice>")));
        Assert.Throws<InvoiceException>(() => InvoiceXmlReader.Parse(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<x>", 70)) + string.Concat(Enumerable.Repeat("</x>", 70)))));
        Assert.Throws<InvoiceException>(() => InvoiceXmlReader.Parse(new byte[InvoiceXmlReader.MaxBytes + 1]));
        string duplicate = Encoding.UTF8.GetString(TestInvoiceFactory.Ubl()).Replace("<cbc:ID>000123</cbc:ID>", "<cbc:ID>000123</cbc:ID><cbc:ID>other</cbc:ID>", StringComparison.Ordinal);
        Assert.Throws<InvoiceException>(() => InvoiceXmlReader.Parse(Encoding.UTF8.GetBytes(duplicate)));
        using var source = new MemoryStream(TestInvoiceFactory.Ubl()); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvoiceXmlReader.ReadAsync(source, new(true)));
    }
}
