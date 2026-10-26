using System.Security.Cryptography;

namespace ContextBar.Application.Packs;

public sealed class PackSignatureVerifier
{
    // Owner release key(s), SubjectPublicKeyInfo/base64. Empty intentionally rejects every production bundle.
    private static readonly string[] ProductionKeys = [];
    private readonly string[] _keys;
    public PackSignatureVerifier() : this(ProductionKeys) { }
    public PackSignatureVerifier(IEnumerable<string> trustedKeys) => _keys = trustedKeys.ToArray();
    public bool Verify(ReadOnlySpan<byte> content, ReadOnlySpan<byte> signature)
    {
        if (signature.Length is 0 or > 128) return false;
        foreach (string encoded in _keys)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(encoded);
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(bytes, out int consumed);
                if (consumed != bytes.Length || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") continue;
                if (key.VerifyData(content, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return true;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException) { }
        }
        return false;
    }
}
