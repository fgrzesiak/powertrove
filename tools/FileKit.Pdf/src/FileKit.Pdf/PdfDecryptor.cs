using System.Security.Cryptography;
using System.Text;

namespace FileKit.Pdf;

internal sealed class PdfDecryptor
{
    private readonly byte[] _encryptionKey;
    private readonly bool _useAes;

    public PdfDecryptor(byte[] encryptionKey, bool useAes)
    {
        _encryptionKey = encryptionKey;
        _useAes = useAes;
    }

    public static readonly byte[] PaddingString = new byte[]
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A
    };

    public static PdfDecryptor? Create(PdfDictionary encrypt, byte[] fileId, string password)
    {
        int v = encrypt["V"] is PdfInteger vInt ? (int)vInt.Value : 0;
        int r = encrypt["R"] is PdfInteger rInt ? (int)rInt.Value : 0;
        int length = encrypt["Length"] is PdfInteger lenInt ? (int)lenInt.Value : 40;
        int p = encrypt["P"] is PdfInteger pInt ? (int)pInt.Value : 0;
        byte[] o = encrypt["O"] is PdfString oStr ? oStr.Value : Array.Empty<byte>();
        byte[] u = encrypt["U"] is PdfString uStr ? uStr.Value : Array.Empty<byte>();
        bool encryptMetadata = encrypt["EncryptMetadata"] is not PdfBoolean mb || mb.Value;

        if (v == 0 || r < 2 || r > 6 || v > 5)
            throw new PdfEncryptedException("Unsupported encryption dictionary version.");

        bool useAes = false;
        if (v == 4 || v == 5)
        {
            var cf = encrypt["CF"] as PdfDictionary;
            var stmf = encrypt["StmF"] as PdfName;
            if (stmf != null && cf != null && stmf.Value != "Identity")
            {
                var cryptFilter = cf[stmf.Value] as PdfDictionary;
                var cfm = cryptFilter?["CFM"] as PdfName;
                if (cfm != null && (cfm.Value == "AESV2" || cfm.Value == "AESV3"))
                    useAes = true;
            }
        }

        byte[] encryptionKey = Array.Empty<byte>();

        if (r == 5 || r == 6)
        {
            useAes = true;
            byte[] oe = encrypt["OE"] is PdfString oeStr ? oeStr.Value : Array.Empty<byte>();
            byte[] ue = encrypt["UE"] is PdfString ueStr ? ueStr.Value : Array.Empty<byte>();

            byte[] pwdUtf8 = Encoding.UTF8.GetBytes(password);
            if (pwdUtf8.Length > 127) pwdUtf8 = pwdUtf8.AsSpan(0, 127).ToArray();

            byte[] expectedHash = Algorithm2bHash(r, pwdUtf8, u.AsSpan(32, 8).ToArray(), Array.Empty<byte>());

            bool userMatch = true;
            for (int i = 0; i < 32; i++) if (u[i] != expectedHash[i]) userMatch = false;

            if (userMatch)
            {
                encryptionKey = DecryptAESFileKey(ue, expectedHash);
            }
            else
            {
                byte[] expectedOHash = Algorithm2bHash(r, pwdUtf8, o.AsSpan(32, 8).ToArray(), u.AsSpan(0, 48).ToArray());

                bool ownerMatch = true;
                for (int i = 0; i < 32; i++) if (o[i] != expectedOHash[i]) ownerMatch = false;

                if (!ownerMatch) return null;

                encryptionKey = DecryptAESFileKey(oe, expectedOHash);
            }
        }
        else
        {
            int lengthBytes = length / 8;
            byte[] userKey = DeriveEncryptionKey(password, o, p, fileId, r, lengthBytes, encryptMetadata);

            byte[] expectedU;
            if (r == 2)
            {
                expectedU = PadPassword("");
                byte[] temp = new byte[32];
                Array.Copy(PaddingString, temp, 32);
                ApplyRC4(userKey, temp, 0, 32);
                expectedU = temp;
            }
            else
            {
                using var md5 = MD5.Create();
                md5.TransformBlock(PaddingString, 0, 32, null, 0);
                md5.TransformFinalBlock(fileId, 0, fileId.Length);
                expectedU = md5.Hash!;

                for (int i = 0; i < 20; i++)
                {
                    byte[] tempKey = new byte[lengthBytes];
                    for (int j = 0; j < lengthBytes; j++) tempKey[j] = (byte)(userKey[j] ^ i);
                    ApplyRC4(tempKey, expectedU, 0, 16);
                }
            }

            bool match = true;
            for (int i = 0; i < 16; i++) if (u[i] != expectedU[i]) match = false;

            if (match)
            {
                encryptionKey = userKey;
            }
            else
            {
                byte[] ownerKey = ComputeOwnerKey(password, r, lengthBytes);
                byte[] oVal = new byte[32];
                Array.Copy(o, oVal, 32);

                if (r == 2)
                {
                    ApplyRC4(ownerKey, oVal, 0, 32);
                }
                else
                {
                    for (int i = 19; i >= 0; i--)
                    {
                        byte[] tempKey = new byte[lengthBytes];
                        for (int j = 0; j < lengthBytes; j++) tempKey[j] = (byte)(ownerKey[j] ^ i);
                        ApplyRC4(tempKey, oVal, 0, 32);
                    }
                }

                encryptionKey = DeriveEncryptionKeyFromPadded(oVal, o, p, fileId, r, lengthBytes, encryptMetadata);

                byte[] expectedOwnerU;
                if (r == 2)
                {
                    expectedOwnerU = PadPassword("");
                    byte[] temp = new byte[32];
                    Array.Copy(PaddingString, temp, 32);
                    ApplyRC4(encryptionKey, temp, 0, 32);
                    expectedOwnerU = temp;
                }
                else
                {
                    using var md5 = MD5.Create();
                    md5.TransformBlock(PaddingString, 0, 32, null, 0);
                    md5.TransformFinalBlock(fileId, 0, fileId.Length);
                    expectedOwnerU = md5.Hash!;

                    for (int i = 0; i < 20; i++)
                    {
                        byte[] tempKey = new byte[lengthBytes];
                        for (int j = 0; j < lengthBytes; j++) tempKey[j] = (byte)(encryptionKey[j] ^ i);
                        ApplyRC4(tempKey, expectedOwnerU, 0, 16);
                    }
                }

                bool ownerMatch = true;
                for (int i = 0; i < 16; i++) if (u[i] != expectedOwnerU[i]) ownerMatch = false;
                if (!ownerMatch) return null;
            }
        }

        return new PdfDecryptor(encryptionKey, useAes);
    }

    public static byte[] Algorithm2bHash(int r, byte[] password, byte[] salt, byte[] udata)
    {
        byte[] kInput = new byte[password.Length + salt.Length + udata.Length];
        Array.Copy(password, 0, kInput, 0, password.Length);
        Array.Copy(salt, 0, kInput, password.Length, salt.Length);
        Array.Copy(udata, 0, kInput, password.Length + salt.Length, udata.Length);

        byte[] k;
        using (var sha256 = SHA256.Create()) k = sha256.ComputeHash(kInput);

        if (r < 6) return k;

        int count = 0;
        while (true)
        {
            count++;
            byte[] k1 = new byte[password.Length + k.Length + udata.Length];
            Array.Copy(password, 0, k1, 0, password.Length);
            Array.Copy(k, 0, k1, password.Length, k.Length);
            Array.Copy(udata, 0, k1, password.Length + k.Length, udata.Length);

            byte[] k1_64 = new byte[k1.Length * 64];
            for (int i = 0; i < 64; i++) Array.Copy(k1, 0, k1_64, i * k1.Length, k1.Length);

            byte[] e;
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                aes.Key = k.AsSpan(0, 16).ToArray();
                aes.IV = k.AsSpan(16, 16).ToArray();
                using var encryptor = aes.CreateEncryptor();
                e = encryptor.TransformFinalBlock(k1_64, 0, k1_64.Length);
            }

            int sum = 0;
            for (int i = 0; i < 16; i++) sum += e[i];

            if (sum % 3 == 0)
            {
                using var sha256 = SHA256.Create();
                k = sha256.ComputeHash(e);
            }
            else if (sum % 3 == 1)
            {
                using var sha384 = SHA384.Create();
                k = sha384.ComputeHash(e);
            }
            else
            {
                using var sha512 = SHA512.Create();
                k = sha512.ComputeHash(e);
            }

            if (count >= 64 && e[e.Length - 1] <= count - 32)
                break;
        }
        return k.AsSpan(0, 32).ToArray();
    }

    private static byte[] DecryptAESFileKey(byte[] encrypted, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = new byte[16];
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
    }

    public byte[] DecryptString(int objectNumber, int generation, byte[] data)
    {
        if (_useAes && _encryptionKey.Length == 32)
        {
            return DecryptDataAES(data, _encryptionKey);
        }

        byte[] objKey = GetObjectKey(objectNumber, generation);
        if (_useAes) return DecryptDataAES(data, objKey);

        byte[] result = new byte[data.Length];
        Array.Copy(data, result, data.Length);
        ApplyRC4(objKey, result, 0, result.Length);
        return result;
    }

    public byte[] DecryptStream(int objectNumber, int generation, byte[] data)
    {
        return DecryptString(objectNumber, generation, data);
    }

    private byte[] GetObjectKey(int objectNumber, int generation)
    {
        byte[] keyInput = new byte[_encryptionKey.Length + 5];
        Array.Copy(_encryptionKey, keyInput, _encryptionKey.Length);
        keyInput[_encryptionKey.Length] = (byte)(objectNumber & 0xFF);
        keyInput[_encryptionKey.Length + 1] = (byte)((objectNumber >> 8) & 0xFF);
        keyInput[_encryptionKey.Length + 2] = (byte)((objectNumber >> 16) & 0xFF);
        keyInput[_encryptionKey.Length + 3] = (byte)(generation & 0xFF);
        keyInput[_encryptionKey.Length + 4] = (byte)((generation >> 8) & 0xFF);

        using var md5 = MD5.Create();
        if (_useAes)
        {
            keyInput = keyInput.Concat(new byte[] { 0x73, 0x41, 0x6C, 0x54 }).ToArray();
            byte[] hash = md5.ComputeHash(keyInput);
            int len = Math.Min(hash.Length, _encryptionKey.Length + 5);
            return hash.AsSpan(0, len).ToArray();
        }
        else
        {
            byte[] hash = md5.ComputeHash(keyInput);
            int len = Math.Min(hash.Length, _encryptionKey.Length + 5);
            return hash.AsSpan(0, len).ToArray();
        }
    }

    private static byte[] DecryptDataAES(byte[] data, byte[] key)
    {
        if (data.Length < 16) return data;
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        byte[] iv = new byte[16];
        Array.Copy(data, iv, 16);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        byte[] decrypted = decryptor.TransformFinalBlock(data, 16, data.Length - 16);

        if (decrypted.Length > 0)
        {
            int pad = decrypted[decrypted.Length - 1];
            if (pad > 0 && pad <= 16 && pad <= decrypted.Length)
            {
                bool valid = true;
                for (int i = 1; i <= pad; i++)
                {
                    if (decrypted[decrypted.Length - i] != pad) { valid = false; break; }
                }
                if (valid)
                {
                    byte[] unpadded = new byte[decrypted.Length - pad];
                    Array.Copy(decrypted, unpadded, unpadded.Length);
                    return unpadded;
                }
            }
        }
        return decrypted;
    }

    public static byte[] GetPasswordBytes(string password)
    {
        byte[] bytes = Encoding.GetEncoding(28591).GetBytes(password);
        return bytes;
    }

    public static byte[] PadPassword(string password)
    {
        byte[] bytes = GetPasswordBytes(password);
        byte[] padded = new byte[32];
        int len = Math.Min(bytes.Length, 32);
        Array.Copy(bytes, padded, len);
        if (len < 32) Array.Copy(PaddingString, 0, padded, len, 32 - len);
        return padded;
    }

    public static byte[] DeriveEncryptionKey(string password, byte[] o, int p, byte[] fileId, int r, int lengthBytes, bool encryptMetadata)
    {
        return DeriveEncryptionKeyFromPadded(PadPassword(password), o, p, fileId, r, lengthBytes, encryptMetadata);
    }

    public static byte[] DeriveEncryptionKeyFromPadded(byte[] padded, byte[] o, int p, byte[] fileId, int r, int lengthBytes, bool encryptMetadata)
    {
        using var md5 = MD5.Create();
        md5.TransformBlock(padded, 0, padded.Length, null, 0);
        md5.TransformBlock(o, 0, o.Length, null, 0);

        byte[] pBytes = BitConverter.GetBytes(p);
        if (!BitConverter.IsLittleEndian) Array.Reverse(pBytes);
        md5.TransformBlock(pBytes, 0, 4, null, 0);

        md5.TransformBlock(fileId, 0, fileId.Length, null, 0);

        if (r >= 4 && !encryptMetadata)
        {
            byte[] noMeta = { 0xFF, 0xFF, 0xFF, 0xFF };
            md5.TransformBlock(noMeta, 0, 4, null, 0);
        }

        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        byte[] hash = md5.Hash!;

        if (r >= 3)
        {
            for (int i = 0; i < 50; i++)
            {
                hash = md5.ComputeHash(hash, 0, lengthBytes);
            }
        }

        byte[] key = new byte[lengthBytes];
        Array.Copy(hash, key, lengthBytes);
        return key;
    }

    public static byte[] ComputeOwnerKey(string ownerPassword, int r, int lengthBytes)
    {
        byte[] padded = PadPassword(ownerPassword);
        using var md5 = MD5.Create();
        md5.TransformBlock(padded, 0, padded.Length, null, 0);
        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        byte[] hash = md5.Hash!;

        if (r >= 3)
        {
            for (int i = 0; i < 50; i++)
            {
                hash = md5.ComputeHash(hash, 0, hash.Length);
            }
        }

        byte[] key = new byte[lengthBytes];
        Array.Copy(hash, key, lengthBytes);
        return key;
    }

    public static void ApplyRC4(byte[] key, byte[] data, int offset, int count)
    {
        if (key.Length == 0) return;

        byte[] s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;

        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) % 256;
            (s[i], s[j]) = (s[j], s[i]);
        }

        int x = 0, y = 0;
        for (int i = 0; i < count; i++)
        {
            x = (x + 1) % 256;
            y = (y + s[x]) % 256;
            (s[x], s[y]) = (s[y], s[x]);
            data[offset + i] ^= s[(s[x] + s[y]) % 256];
        }
    }
}
