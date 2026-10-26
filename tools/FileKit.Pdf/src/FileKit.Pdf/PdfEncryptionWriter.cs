using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FileKit.Pdf;

/// <summary>Reader-enforced PDF permissions; these are not a substitute for access control.</summary>
public sealed record PdfPermissions(bool AllowPrinting = true, bool AllowCopying = false);

/// <summary>ISO 32000-2 Standard Security Handler, AES-256 /V 5 /R 6 /AESV3 only.</summary>
public sealed record PdfEncryptionOptions(
    string UserPassword,
    string? OwnerPassword = null,
    PdfPermissions? Permissions = null);

/// <summary>Per-output encryption state. Never persisted outside the protected document.</summary>
internal sealed class PdfEncryptionWriter
{
    private readonly byte[] _fileKey = RandomNumberGenerator.GetBytes(32);
    public byte[] DocumentId { get; } = RandomNumberGenerator.GetBytes(16);
    public PdfDictionary EncryptionDictionary { get; }

    public PdfEncryptionWriter(PdfEncryptionOptions options)
    {
        if (string.IsNullOrEmpty(options.UserPassword))
            throw new PdfOperationException("A password is required.");
        string ownerPassword = options.OwnerPassword ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        byte[] user = PasswordBytes(options.UserPassword);
        byte[] owner = PasswordBytes(ownerPassword);
        try
        {
            byte[] uv = RandomNumberGenerator.GetBytes(8), uk = RandomNumberGenerator.GetBytes(8);
            byte[] ov = RandomNumberGenerator.GetBytes(8), ok = RandomNumberGenerator.GetBytes(8);

            byte[] u = BuildEntry(PdfDecryptor.Algorithm2bHash(6, user, uv, []), uv, uk);
            byte[] o = BuildEntry(PdfDecryptor.Algorithm2bHash(6, owner, ov, u), ov, ok);
            byte[] ue = WrapKey(PdfDecryptor.Algorithm2bHash(6, user, uk, []), _fileKey);
            byte[] oe = WrapKey(PdfDecryptor.Algorithm2bHash(6, owner, ok, u), _fileKey);

            PdfPermissions permissions = options.Permissions ?? new();
            // ISO 32000-2 7.6.3.2: permission bits 3 and 12 for printing; 5 for copying.
            int p = unchecked((int)0xFFFFF0C0);
            if (permissions.AllowPrinting) p |= 0x804;
            if (permissions.AllowCopying) p |= 0x10;

            byte[] clearPerms = RandomNumberGenerator.GetBytes(16);
            BinaryPrimitives.WriteInt32LittleEndian(clearPerms.AsSpan(0, 4), p);
            clearPerms.AsSpan(4, 4).Fill(0xFF);
            clearPerms[8] = (byte)'T'; // EncryptMetadata = true
            clearPerms[9] = (byte)'a'; clearPerms[10] = (byte)'d'; clearPerms[11] = (byte)'b';
            byte[] perms = AesEncrypt(_fileKey, new byte[16], clearPerms, false, CipherMode.ECB);

            EncryptionDictionary = new PdfDictionary
            {
                ["Filter"] = new PdfName("Standard"),
                ["V"] = new PdfInteger(5),
                ["R"] = new PdfInteger(6),
                ["Length"] = new PdfInteger(256),
                ["P"] = new PdfInteger(p),
                ["EncryptMetadata"] = PdfBoolean.True,
                ["StmF"] = new PdfName("StdCF"),
                ["StrF"] = new PdfName("StdCF"),
                ["CF"] = new PdfDictionary
                {
                    ["StdCF"] = new PdfDictionary
                    {
                        ["Type"] = new PdfName("CryptFilter"),
                        ["CFM"] = new PdfName("AESV3"),
                        ["AuthEvent"] = new PdfName("DocOpen"),
                        ["Length"] = new PdfInteger(32)
                    }
                },
                ["O"] = new PdfString(o, true),
                ["U"] = new PdfString(u, true),
                ["OE"] = new PdfString(oe, true),
                ["UE"] = new PdfString(ue, true),
                ["Perms"] = new PdfString(perms, true)
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(user);
            CryptographicOperations.ZeroMemory(owner);
        }
    }

    private static byte[] PasswordBytes(string password)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(password);
        if (bytes.Length <= 127) return bytes;
        var cut = bytes.AsSpan(0, 127).ToArray();
        CryptographicOperations.ZeroMemory(bytes);
        return cut;
    }

    private static byte[] BuildEntry(byte[] hash, byte[] validationSalt, byte[] keySalt)
    {
        var result = new byte[48];
        hash.AsSpan(0, 32).CopyTo(result);
        validationSalt.CopyTo(result, 32);
        keySalt.CopyTo(result, 40);
        return result;
    }

    private static byte[] WrapKey(byte[] key, byte[] fileKey) =>
        AesEncrypt(key, new byte[16], fileKey, false, CipherMode.CBC);

    private static byte[] AesEncrypt(byte[] key, byte[] iv, byte[] plaintext, bool padded, CipherMode mode)
    {
        using Aes aes = Aes.Create();
        aes.Mode = mode;
        aes.Padding = padded ? PaddingMode.PKCS7 : PaddingMode.None;
        aes.Key = key;
        if (mode == CipherMode.CBC) aes.IV = iv;
        using ICryptoTransform encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    private byte[] EncryptData(byte[] plaintext)
    {
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        byte[] ciphertext = AesEncrypt(_fileKey, iv, plaintext, true, CipherMode.CBC);
        var output = new byte[iv.Length + ciphertext.Length];
        iv.CopyTo(output, 0);
        ciphertext.CopyTo(output, iv.Length);
        return output;
    }

    public PdfObject EncryptObject(PdfObject value, int depth = 0)
    {
        if (depth > 200) throw new PdfLimitExceededException("Object nesting is too deep.");
        switch (value)
        {
            case PdfString s: return new PdfString(EncryptData(s.Value), true);
            case PdfStream stream:
                var dictionary = (PdfDictionary)EncryptObject(stream.Dictionary, depth + 1);
                dictionary.Remove("Length"); // serialiser writes the encrypted stream length
                return new PdfStream(dictionary, EncryptData(stream.Data));
            case PdfDictionary dict:
                var newDict = new PdfDictionary();
                foreach (var (name, item) in dict.Entries)
                    newDict[name] = EncryptObject(item, depth + 1);
                return newDict;
            case PdfArray array:
                return new PdfArray(array.Items.Select(item => EncryptObject(item, depth + 1)));
            default:
                return value; // references, names, numbers, booleans, null remain as written
        }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_fileKey);
}
