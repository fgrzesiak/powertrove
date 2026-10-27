namespace FileKit.EInvoice;

public sealed class InvoiceException(string message, Exception? inner = null) : Exception(message, inner);
public sealed record InvoiceParty(string Name, string Address, string TaxId, string RegistrationId, string Email);
public sealed record InvoiceLine(string Id, string Description, decimal Quantity, string Unit, decimal? UnitPrice, decimal? PriceBaseQuantity, decimal NetAmount, string TaxCategory, decimal? TaxRate);
public sealed record InvoiceTax(string Category, decimal? Rate, decimal TaxableAmount, decimal TaxAmount, string ExemptionReason);
public sealed record InvoiceTotals(decimal? LineNet, decimal? Allowances, decimal? Charges, decimal Net, decimal Tax, decimal Gross, decimal? Prepaid, decimal? Rounding, decimal Payable);
public sealed record InvoicePayment(string Code, string Account, string Reference, string Terms);
public sealed record Invoice(string Syntax, string Profile, string Id, DateOnly IssueDate, string TypeCode, string Currency, string BuyerReference,
    InvoiceParty Seller, InvoiceParty Buyer, IReadOnlyList<InvoiceLine> Lines, IReadOnlyList<InvoiceTax> Taxes, InvoiceTotals Totals, IReadOnlyList<InvoicePayment> Payments, string Notes);
