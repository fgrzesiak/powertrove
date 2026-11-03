# Changelog

## 0.1.0

- Added a bounded, offline RFC 5322 and MIME parser using only the .NET BCL.
- Added RFC 2047 encoded headers and RFC 2231 extended/continuation parameters.
- Added multipart/mixed, alternative and related processing, CID image content and attachment extraction.
- Added base64, quoted-printable, 7bit, 8bit and .NET codepage charset support.
- Added tests for existing TestEmlFactory fixtures, encodings, malformed messages, limits and cancellation.
