using System.Text;
using Xunit;

namespace FileKit.Pdf.Tests;

public class PdfDecryptionTests
{
    [Fact]
    public void DeriveEncryptionKey_KnownAnswer_R2()
    {
        // Vector derived independently using standard R2 RC4-40
        string password = "user";
        byte[] o = new byte[32];
        for(int i = 0; i < 32; i++) o[i] = (byte)i;
        int p = -4;
        byte[] fileId = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };

        byte[] key = PdfDecryptor.DeriveEncryptionKey(password, o, p, fileId, 2, 5, true);

        // Python derivation output: 76015d74a0
        string hexKey = BitConverter.ToString(key).Replace("-", "").ToLowerInvariant();
        Assert.Equal("76015d74a0", hexKey);
    }

    [Fact]
    public void DeriveEncryptionKey_KnownAnswer_R3()
    {
        // Vector derived independently using standard R3 RC4-128
        string password = "user";
        byte[] o = new byte[32];
        for(int i = 0; i < 32; i++) o[i] = (byte)i;
        int p = -4;
        byte[] fileId = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };

        byte[] key = PdfDecryptor.DeriveEncryptionKey(password, o, p, fileId, 3, 16, true);

        // Python derivation output: d9572ad4948ae87a285cbff2d750fa98
        string hexKey = BitConverter.ToString(key).Replace("-", "").ToLowerInvariant();
        Assert.Equal("d9572ad4948ae87a285cbff2d750fa98", hexKey);
    }

    [Fact]
    public void Algorithm2bHash_KnownAnswer_R6()
    {
        // Vector derived independently using standard R6 AES-256
        byte[] pwd = Encoding.UTF8.GetBytes("user");
        byte[] salt = new byte[8];
        for(int i = 0; i < 8; i++) salt[i] = (byte)i;

        byte[] hash = PdfDecryptor.Algorithm2bHash(6, pwd, salt, Array.Empty<byte>());

        // Python derivation output for (user, salt, empty udata)
        string hexHash = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        Assert.Equal("731758c09c8b0160a34721d18bdd24220abada0070aa3f05b8103fd5b8d05f17", hexHash);

        byte[] ownerPwd = Encoding.UTF8.GetBytes("owner");
        byte[] oSalt = new byte[8];
        for(int i = 0; i < 8; i++) oSalt[i] = (byte)(i + 8);
        byte[] udata = Encoding.UTF8.GetBytes("user");

        byte[] ownerHash = PdfDecryptor.Algorithm2bHash(6, ownerPwd, oSalt, udata);
        string hexOwnerHash = BitConverter.ToString(ownerHash).Replace("-", "").ToLowerInvariant();
        Assert.Equal("26197cb96cd28656ccf2d8e1c3120f9061856112a216af853f63721d919313c7", hexOwnerHash);
    }

    [Theory]
    [InlineData(2, "user", "owner")]
    [InlineData(3, "user", "owner")]
    [InlineData(4, "user", "owner")]
    [InlineData(6, "user", "owner")]
    [InlineData(2, "", "owner")]
    [InlineData(3, "", "owner")]
    [InlineData(4, "", "owner")]
    [InlineData(6, "", "owner")]
    public void EncryptedFixtures_DecryptWithUserOrOwner(int r, string userPwd, string ownerPwd)
    {
        var dummyData = TestPdfFactory.Serialize(new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R >>" }, "/Root 1 0 R");
        byte[] encryptedData = PdfTestEncryptor.Encrypt(dummyData, r, userPwd, ownerPwd);

        // Can decrypt with correct user password
        var docUser = PdfDocument.Load(encryptedData, password: userPwd);
        Assert.NotNull(docUser);
        if (string.IsNullOrEmpty(userPwd))
            Assert.True(docUser.OpenedWithEmptyPassword);

        // Can decrypt with correct owner password
        var docOwner = PdfDocument.Load(encryptedData, password: ownerPwd);
        Assert.NotNull(docOwner);

        // Throws for incorrect password
        var ex = Assert.Throws<PdfPasswordRequiredException>(() => PdfDocument.Load(encryptedData, password: "wrong"));
        if (!string.IsNullOrEmpty(userPwd)) Assert.DoesNotContain(userPwd, ex.Message);
        Assert.DoesNotContain(ownerPwd, ex.Message);
        Assert.DoesNotContain("wrong", ex.Message);
    }

    [Fact]
    public void DecryptedOutputs_DoNotContainEncrypt_AndCanReload()
    {
        var dummyData = TestPdfFactory.Serialize(new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R >>" }, "/Root 1 0 R");
        byte[] encryptedData = PdfTestEncryptor.Encrypt(dummyData, 4, "secret", "owner");

        var validDoc = PdfDocument.Load(encryptedData, password: "secret");

        using var ms = new MemoryStream();
        var valBuilder = new PdfDocumentBuilder();
        valBuilder.AddPage(validDoc, 0);

        valBuilder.Save(ms);
        byte[] outData = ms.ToArray();

        string outStr = Encoding.ASCII.GetString(outData);
        Assert.DoesNotContain("/Encrypt", outStr);

        // Reloads without password
        var reloadDoc = PdfDocument.Load(outData);
        Assert.NotNull(reloadDoc);
    }

    [Fact]
    public void Document_PasswordRequiredException_ThrownForWrongPassword_WithoutPasswordInMessage()
    {
        var dummyData = TestPdfFactory.Serialize(new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R >>" }, "/Root 1 0 R");
        byte[] encryptedData = PdfTestEncryptor.Encrypt(dummyData, 2, "user", "owner");

        var ex = Assert.Throws<PdfPasswordRequiredException>(() => PdfDocument.Load(encryptedData));
        Assert.DoesNotContain("user", ex.Message);

        var ex2 = Assert.Throws<PdfPasswordRequiredException>(() => PdfDocument.Load(encryptedData, password: "wrongpassword"));
        Assert.DoesNotContain("wrongpassword", ex2.Message);
    }

    [Fact]
    public void MutationTests_OverEncryptedFixtures_ThrowPdfException()
    {
        var dummyData = TestPdfFactory.Serialize(new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R >>" }, "/Root 1 0 R");
        byte[] encryptedData = PdfTestEncryptor.Encrypt(dummyData, 4, "user", "owner");

        string encStr = Encoding.ASCII.GetString(encryptedData);
        int idx = encStr.IndexOf("/U <");
        if (idx >= 0)
        {
            encryptedData[idx + 5] = (byte)(encryptedData[idx + 5] ^ 1);
            Assert.Throws<PdfPasswordRequiredException>(() => PdfDocument.Load(encryptedData, password: "user"));
        }
    }
}
