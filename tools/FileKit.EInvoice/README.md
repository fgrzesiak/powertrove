# FileKit.EInvoice

BCL-only bounded UBL Invoice/CreditNote and CII 100 invoice projection for local
viewing/export. DTDs, external entities, excessive XML and unsupported structures
fail explicitly. Decimal values are parsed with invariant XML notation and retained
as decimals, never recomputed or converted to floating point.

This is selected-field parsing, not XSD, Schematron, EN16931, XRechnung, PDF/A or
signature validation. Successful parsing must never be presented as conformance.
The host supplies all localized labels and owns safe output publication.
