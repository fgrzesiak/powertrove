using System.Text;
using System.Xml.Linq;
namespace FileKit.EInvoice.Testing;

internal static class TestInvoiceFactory
{
    internal static byte[] Ubl(string amount = "100.00", string description = "Coffee & tea")
    {
        string safe = new XElement("x", description).ToString(SaveOptions.DisableFormatting)[3..^4];
        string xml = "<Invoice xmlns='urn:oasis:names:specification:ubl:schema:xsd:Invoice-2' xmlns:cac='urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2' xmlns:cbc='urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2'>" +
            "<cbc:CustomizationID>urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0</cbc:CustomizationID><cbc:ID>000123</cbc:ID><cbc:IssueDate>2026-10-27</cbc:IssueDate><cbc:InvoiceTypeCode>380</cbc:InvoiceTypeCode><cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode><cbc:BuyerReference>R-1</cbc:BuyerReference>" +
            Party("AccountingSupplierParty", "Seller Grüße") + Party("AccountingCustomerParty", "Buyer") +
            "<cac:TaxTotal><cbc:TaxAmount currencyID='EUR'>19.00</cbc:TaxAmount><cac:TaxSubtotal><cbc:TaxableAmount currencyID='EUR'>100.00</cbc:TaxableAmount><cbc:TaxAmount currencyID='EUR'>19.00</cbc:TaxAmount><cac:TaxCategory><cbc:ID>S</cbc:ID><cbc:Percent>19</cbc:Percent></cac:TaxCategory></cac:TaxSubtotal></cac:TaxTotal>" +
            "<cac:LegalMonetaryTotal><cbc:LineExtensionAmount currencyID='EUR'>100.00</cbc:LineExtensionAmount><cbc:TaxExclusiveAmount currencyID='EUR'>100.00</cbc:TaxExclusiveAmount><cbc:TaxInclusiveAmount currencyID='EUR'>119.00</cbc:TaxInclusiveAmount><cbc:PrepaidAmount currencyID='EUR'>20.00</cbc:PrepaidAmount><cbc:PayableAmount currencyID='EUR'>99.00</cbc:PayableAmount></cac:LegalMonetaryTotal>" +
            "<cac:InvoiceLine><cbc:ID>001</cbc:ID><cbc:InvoicedQuantity unitCode='C62'>2</cbc:InvoicedQuantity><cbc:LineExtensionAmount currencyID='EUR'>" + amount + "</cbc:LineExtensionAmount><cac:Item><cbc:Name>" + safe + "</cbc:Name><cac:ClassifiedTaxCategory><cbc:ID>S</cbc:ID><cbc:Percent>19</cbc:Percent></cac:ClassifiedTaxCategory></cac:Item><cac:Price><cbc:PriceAmount currencyID='EUR'>50.00</cbc:PriceAmount><cbc:BaseQuantity>1</cbc:BaseQuantity></cac:Price></cac:InvoiceLine></Invoice>";
        return Encoding.UTF8.GetBytes(xml);
        static string Party(string tag, string name) => "<cac:" + tag + "><cac:Party><cac:PartyLegalEntity><cbc:RegistrationName>" + name + "</cbc:RegistrationName></cac:PartyLegalEntity><cac:PostalAddress><cbc:StreetName>Example 1</cbc:StreetName><cbc:CityName>Berlin</cbc:CityName></cac:PostalAddress></cac:Party></cac:" + tag + ">";
    }
    internal static byte[] Cii() => Encoding.UTF8.GetBytes("""
        <rsm:CrossIndustryInvoice xmlns:rsm="urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100" xmlns:ram="urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100" xmlns:udt="urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100">
          <rsm:ExchangedDocumentContext><ram:GuidelineSpecifiedDocumentContextParameter><ram:ID>urn:cen.eu:en16931:2017</ram:ID></ram:GuidelineSpecifiedDocumentContextParameter></rsm:ExchangedDocumentContext>
          <rsm:ExchangedDocument><ram:ID>000123</ram:ID><ram:TypeCode>380</ram:TypeCode><ram:IssueDateTime><udt:DateTimeString format="102">20261027</udt:DateTimeString></ram:IssueDateTime></rsm:ExchangedDocument>
          <rsm:SupplyChainTradeTransaction>
            <ram:IncludedSupplyChainTradeLineItem>
              <ram:AssociatedDocumentLineDocument><ram:LineID>001</ram:LineID></ram:AssociatedDocumentLineDocument><ram:SpecifiedTradeProduct><ram:Name>Coffee &amp; tea</ram:Name></ram:SpecifiedTradeProduct>
              <ram:SpecifiedLineTradeAgreement><ram:NetPriceProductTradePrice><ram:ChargeAmount>50.00</ram:ChargeAmount><ram:BasisQuantity>1</ram:BasisQuantity></ram:NetPriceProductTradePrice></ram:SpecifiedLineTradeAgreement>
              <ram:SpecifiedLineTradeDelivery><ram:BilledQuantity unitCode="C62">2</ram:BilledQuantity></ram:SpecifiedLineTradeDelivery>
              <ram:SpecifiedLineTradeSettlement><ram:ApplicableTradeTax><ram:CategoryCode>S</ram:CategoryCode><ram:RateApplicablePercent>19</ram:RateApplicablePercent></ram:ApplicableTradeTax><ram:SpecifiedTradeSettlementLineMonetarySummation><ram:LineTotalAmount>100.00</ram:LineTotalAmount></ram:SpecifiedTradeSettlementLineMonetarySummation></ram:SpecifiedLineTradeSettlement>
            </ram:IncludedSupplyChainTradeLineItem>
            <ram:ApplicableHeaderTradeAgreement><ram:BuyerReference>R-1</ram:BuyerReference><ram:SellerTradeParty><ram:Name>Seller Grüße</ram:Name></ram:SellerTradeParty><ram:BuyerTradeParty><ram:Name>Buyer</ram:Name></ram:BuyerTradeParty></ram:ApplicableHeaderTradeAgreement>
            <ram:ApplicableHeaderTradeSettlement><ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode><ram:ApplicableTradeTax><ram:CalculatedAmount>19.00</ram:CalculatedAmount><ram:BasisAmount>100.00</ram:BasisAmount><ram:CategoryCode>S</ram:CategoryCode><ram:RateApplicablePercent>19</ram:RateApplicablePercent></ram:ApplicableTradeTax><ram:SpecifiedTradeSettlementHeaderMonetarySummation><ram:LineTotalAmount>100.00</ram:LineTotalAmount><ram:TaxBasisTotalAmount>100.00</ram:TaxBasisTotalAmount><ram:TaxTotalAmount currencyID="EUR">19.00</ram:TaxTotalAmount><ram:GrandTotalAmount>119.00</ram:GrandTotalAmount><ram:TotalPrepaidAmount>20.00</ram:TotalPrepaidAmount><ram:DuePayableAmount>99.00</ram:DuePayableAmount></ram:SpecifiedTradeSettlementHeaderMonetarySummation></ram:ApplicableHeaderTradeSettlement>
          </rsm:SupplyChainTradeTransaction>
        </rsm:CrossIndustryInvoice>
        """);
}
