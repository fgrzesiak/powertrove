using System.Security.Cryptography;
using System.Text;

namespace FileKit.Pdf.Tests;

/// <summary>Test-only encryptor for generating ISO 32000 algorithm fixtures (R2-R6).</summary>
public static class PdfTestEncryptor
{
    public static byte[] Encrypt(byte[] pdfData, int r, string userPassword, string ownerPassword, bool encryptMetadata = true)
    {
        var doc = PdfDocument.Load(pdfData);
        var encryptDict = new PdfDictionary();
        encryptDict["Filter"] = new PdfName("Standard");

        int v = 0;
        int lengthBytes = 0;
        if (r == 2) { v = 1; lengthBytes = 5; }
        else if (r == 3) { v = 2; lengthBytes = 16; }
        else if (r == 4) { v = 4; lengthBytes = 16; }
        else if (r == 6) { v = 5; lengthBytes = 32; }
        else throw new NotSupportedException($"R={r} not supported in test encryptor");

        encryptDict["V"] = new PdfInteger(v);
        encryptDict["R"] = new PdfInteger(r);
        encryptDict["Length"] = new PdfInteger(lengthBytes * 8);
        encryptDict["P"] = new PdfInteger(-4);
        if (!encryptMetadata) encryptDict["EncryptMetadata"] = PdfBoolean.False;

        byte[] fileId = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };

        byte[] o, u, oe = Array.Empty<byte>(), ue = Array.Empty<byte>(), perms = Array.Empty<byte>();

        if (r == 6)
        {
            encryptDict["StmF"] = new PdfName("StdCF");
            encryptDict["StrF"] = new PdfName("StdCF");
            var cfDict = new PdfDictionary();
            var stdCf = new PdfDictionary();
            stdCf["CFM"] = new PdfName("AESV3");
            stdCf["AuthEvent"] = new PdfName("DocOpen");
            stdCf["Length"] = new PdfInteger(32);
            cfDict["StdCF"] = stdCf;
            encryptDict["CF"] = cfDict;

            byte[] pwdUtf8 = Encoding.UTF8.GetBytes(userPassword);
            if (pwdUtf8.Length > 127) pwdUtf8 = pwdUtf8.AsSpan(0, 127).ToArray();

            byte[] ownerUtf8 = Encoding.UTF8.GetBytes(ownerPassword);
            if (ownerUtf8.Length > 127) ownerUtf8 = ownerUtf8.AsSpan(0, 127).ToArray();

            byte[] uSalt = new byte[8], oSalt = new byte[8], keySalt = new byte[8];
            for (int i = 0; i < 8; i++)
            {
                uSalt[i] = (byte)i;
                oSalt[i] = (byte)(i + 8);
                keySalt[i] = (byte)(i + 16);
            }

            byte[] userHash = PdfDecryptor.Algorithm2bHash(6, pwdUtf8, uSalt, Array.Empty<byte>());
            u = new byte[48];
            Array.Copy(userHash, 0, u, 0, 32);
            Array.Copy(uSalt, 0, u, 32, 8);
            Array.Copy(keySalt, 0, u, 40, 8);

            byte[] ownerHash = PdfDecryptor.Algorithm2bHash(6, ownerUtf8, oSalt, u);
            o = new byte[48];
            Array.Copy(ownerHash, 0, o, 0, 32);
            Array.Copy(oSalt, 0, o, 32, 8);
            Array.Copy(keySalt, 0, o, 40, 8);

            byte[] fileKey = new byte[32];
            for (int i = 0; i < 32; i++) fileKey[i] = (byte)(100 + i);

            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                aes.Key = userHash;
                aes.IV = new byte[16];
                using var encryptor = aes.CreateEncryptor();
                ue = encryptor.TransformFinalBlock(fileKey, 0, 32);
            }

            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                aes.Key = ownerHash;
                aes.IV = new byte[16];
                using var encryptor = aes.CreateEncryptor();
                oe = encryptor.TransformFinalBlock(fileKey, 0, 32);
            }

            encryptDict["OE"] = new PdfString(oe);
            encryptDict["UE"] = new PdfString(ue);

            // Generate valid Perms for R6
            // Perms is 16 bytes: permissions (4 bytes) + 12 bytes padding (1s or random)
            byte[] pBytes = BitConverter.GetBytes(-4);
            if (!BitConverter.IsLittleEndian) Array.Reverse(pBytes);
            byte[] permsClear = new byte[16];
            Array.Copy(pBytes, permsClear, 4);
            for(int i=4; i<16; i++) permsClear[i] = 0xFF; // Padding
            permsClear[4] = (byte)'T';
            permsClear[5] = (byte)'O';
            permsClear[6] = (byte)'D';
            permsClear[7] = (byte)'O';

            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB; // Perms in R6 uses AES-ECB
                aes.Padding = PaddingMode.None;
                aes.Key = fileKey; // Perms is encrypted with the file key
                using var encryptor = aes.CreateEncryptor();
                perms = encryptor.TransformFinalBlock(permsClear, 0, 16);
            }
            encryptDict["Perms"] = new PdfString(perms);
        }
        else
        {
            if (r == 4)
            {
                encryptDict["StmF"] = new PdfName("StdCF");
                encryptDict["StrF"] = new PdfName("StdCF");
                var cfDict = new PdfDictionary();
                var stdCf = new PdfDictionary();
                stdCf["CFM"] = new PdfName("AESV2");
                stdCf["AuthEvent"] = new PdfName("DocOpen");
                stdCf["Length"] = new PdfInteger(16);
                cfDict["StdCF"] = stdCf;
                encryptDict["CF"] = cfDict;
            }

            byte[] ownerKey = PdfDecryptor.ComputeOwnerKey(ownerPassword, r, lengthBytes);
            o = PdfDecryptor.PadPassword(userPassword);

            if (r == 2)
            {
                PdfDecryptor.ApplyRC4(ownerKey, o, 0, 32);
            }
            else
            {
                for (int i = 0; i < 20; i++)
                {
                    byte[] tempKey = new byte[lengthBytes];
                    for (int j = 0; j < lengthBytes; j++) tempKey[j] = (byte)(ownerKey[j] ^ i);
                    PdfDecryptor.ApplyRC4(tempKey, o, 0, 32);
                }
            }

            byte[] encKey = PdfDecryptor.DeriveEncryptionKey(userPassword, o, -4, fileId, r, lengthBytes, encryptMetadata);

            if (r == 2)
            {
                u = PdfDecryptor.PadPassword("");
                PdfDecryptor.ApplyRC4(encKey, u, 0, 32);
            }
            else
            {
                using var md5 = MD5.Create();
                md5.TransformBlock(PdfDecryptor.PaddingString, 0, 32, null, 0);
                md5.TransformFinalBlock(fileId, 0, fileId.Length);
                u = md5.Hash!;

                for (int i = 0; i < 20; i++)
                {
                    byte[] tempKey = new byte[lengthBytes];
                    for (int j = 0; j < lengthBytes; j++) tempKey[j] = (byte)(encKey[j] ^ i);
                    PdfDecryptor.ApplyRC4(tempKey, u, 0, 16);
                }
                byte[] uFull = new byte[32];
                Array.Copy(u, uFull, 16);
                u = uFull;
            }
        }

        encryptDict["O"] = new PdfString(o);
        encryptDict["U"] = new PdfString(u);

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R >>"
        };

        using var ms = new MemoryStream();
        PdfSerializer.Write(ms, encryptDict);
        string encryptStr = Encoding.ASCII.GetString(ms.ToArray());
        string rootObjStr = $"/Root 1 0 R /Encrypt {encryptStr} /ID [<0102030405060708090A0B0C0D0E0F10> <0102030405060708090A0B0C0D0E0F10>]";

        return TestPdfFactory.Serialize(objects, rootObjStr);
    }
}
