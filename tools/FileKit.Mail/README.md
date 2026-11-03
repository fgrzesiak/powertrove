# FileKit.Mail

A dependency-free, offline RFC 5322/MIME message parser for PowerTrove.

Supports folded headers, RFC 2047 encoded words, RFC 2231 filename and charset parameters, nested multipart/mixed, multipart/alternative and multipart/related, CID-bearing inline images, attachments and base64/quoted-printable/7bit/8bit transfer encodings. .NET's built-in CodePagesEncodingProvider supplies Windows-125x and ISO-8859-x compatibility.

The parser returns **data**, not executable mail content. It does not fetch remote images, process active HTML, decrypt PGP/S/MIME, verify signatures, read mailboxes or contact the network. The existing PDF Mail adapter retains the AngleSharp HTML sanitizer and CSP-protected Chromium printing.

Limits default to 25 MiB input, 32 levels of nesting, 1,000 MIME parts, 2,200 delimiters, 256 headers, 32,768 bytes per header value, 100 attachments, 10 MiB per attachment, 2 MiB per inline image, 5 MiB per body and 25 MiB total decoded. A CancellationToken is checked during parsing and decoding.

Run tests locally with `dotnet test tools/FileKit.Mail/FileKit.Mail.slnx -c Release`. Existing EML fixtures are linked from `tools/FileKit.Html/testing/TestEmlFactory.cs`. No cloud tests, external services or NuGet dependencies are required by the library project.

This is an intentionally bounded MIME subset; cryptographic MIME and transfer encodings outside the supported list are rejected.
