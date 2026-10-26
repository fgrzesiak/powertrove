using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using ContextBar.Abstractions.Office;
using ContextBar.Infrastructure.Files;

namespace ContextBar.Infrastructure.Tests;

public sealed class OfficeInputValidatorTests
{
    [Theory]
    [InlineData(".docx", OfficeApplication.Word, "word/document.xml")]
    [InlineData(".xlsx", OfficeApplication.Excel, "xl/workbook.xml")]
    [InlineData(".pptx", OfficeApplication.PowerPoint, "ppt/presentation.xml")]
    public void ValidPackageWithRequiredPart_IsAccepted(string extension, OfficeApplication app, string part) =>
        OfficeInputValidator.Validate(Package(("[Content_Types].xml", "<Types />"), (part, "<root />")), extension, app, false);

    [Theory]
    [InlineData("xl/macrosheets/sheet1.xml", "<root />")]
    [InlineData("xl/vbaProject.bin", "binary")]
    [InlineData("[Content_Types].xml", "<Types><Override ContentType='application/vnd.ms-excel.macrosheet+xml' /></Types>")]
    [InlineData("xl/_rels/workbook.xml.rels", "<Relationships><Relationship Type='http://schemas.microsoft.com/office/2006/relationships/xlMacrosheet' /></Relationships>")]
    public void MacroContent_IsRejectedBeforeOffice(string part, string content)
    {
        var parts = new Dictionary<string, string> { ["[Content_Types].xml"] = "<Types />", ["xl/workbook.xml"] = "<root />", [part] = content };
        var ex = Assert.Throws<OfficeConversionException>(() => OfficeInputValidator.Validate(Package(parts.Select(x => (x.Key, x.Value)).ToArray()), ".xlsx", OfficeApplication.Excel, false));
        Assert.Equal(OfficeFailure.UnsafeMacros, ex.Failure);
    }

    [Theory]
    [InlineData("a,b\n1,=SUM(1:1)")]
    [InlineData("a,b\n1,\"  +cmd\"")]
    [InlineData("a,b\n1,\t@SUM(1:1)")]
    [InlineData("a,b\n1,-3")]
    public void FormulaCapableCsv_IsRejected(string csv) => Assert.Equal(OfficeFailure.UnsafeMacros,
        Assert.Throws<OfficeConversionException>(() => OfficeInputValidator.Validate(Encoding.UTF8.GetBytes(csv), ".csv", OfficeApplication.Excel, false)).Failure);

    [Fact]
    public void PlainCsvWithQuotesAndLineBreaks_IsAccepted() => OfficeInputValidator.Validate("name,value\n\"a,b\",3\n\"line\nline\",4"u8.ToArray(), ".csv", OfficeApplication.Excel, false);

    [Fact]
    public void ExternalRelationshipsAndDoctype_AreRejected()
    {
        foreach (string relationships in new[] { "<Relationships><Relationship TargetMode='External' Target='https://invalid.example/' /></Relationships>", "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///secret'>]><x>&e;</x>" })
            Assert.Throws<OfficeConversionException>(() => OfficeInputValidator.Validate(Package(("[Content_Types].xml", "<Types />"), ("word/document.xml", "<root />"), ("word/_rels/document.xml.rels", relationships)), ".docx", OfficeApplication.Word, false));
    }

    [Fact]
    public void LegacyWorkbook_RejectsXlmAndEncryption()
    {
        OfficeInputValidator.Validate(Compound("Workbook", Workbook(false, false)), ".xls", OfficeApplication.Excel, false);
        var xlm = Assert.Throws<OfficeConversionException>(() => OfficeInputValidator.Validate(Compound("Workbook", Workbook(true, false)), ".xls", OfficeApplication.Excel, false));
        var encrypted = Assert.Throws<OfficeConversionException>(() => OfficeInputValidator.Validate(Compound("Workbook", Workbook(false, true)), ".xls", OfficeApplication.Excel, false));
        Assert.Equal(OfficeFailure.UnsafeMacros, xlm.Failure); Assert.Equal(OfficeFailure.PasswordProtected, encrypted.Failure);
    }

    [Fact]
    public void LegacyWord_RejectsPasswordBit()
    {
        byte[] word = new byte[4096]; Put16(word, 0, 0xA5EC); Put16(word, 10, 0x0100);
        Assert.Equal(OfficeFailure.PasswordProtected, Assert.Throws<OfficeConversionException>(() => OfficeInputValidator.Validate(Compound("WordDocument", word), ".doc", OfficeApplication.Word, false)).Failure);
    }

    [Fact]
    public void CyclicFatAndMalformedContainers_FailClosed()
    {
        var cyclic = Compound("Workbook", Workbook(false, false)); Put32(cyclic, 512 + 2 * 4, 2);
        var random = new Random(117);
        var inputs = new List<byte[]> { cyclic, Array.Empty<byte>(), "%PDF-1.7"u8.ToArray() };
        for (int n = 1; n < 1024; n += 31) { byte[] bytes = new byte[n]; random.NextBytes(bytes); inputs.Add(bytes); }
        foreach (var input in inputs) Assert.Throws<OfficeConversionException>(() => OfficeInputValidator.Validate(input, ".xls", OfficeApplication.Excel, false));
    }

    [Theory]
    [InlineData("en-US", "password-protected")]
    [InlineData("de-DE", "kennwortgeschützt")]
    public async Task VerificationFailure_IsLocalizedAndDoesNotIncludeNames(string culture, string fragment)
    {
        using var dir = new TempDirectory(); byte[] word = new byte[4096]; Put16(word, 0, 0xA5EC); Put16(word, 10, 0x0100);
        string source = dir.Write("private document.doc", Compound("WordDocument", word));
        var old = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            var check = await new FileSignatureVerifier().VerifyAsync(source, new FileInputFilter("documents", OfficeFormats.WordExtensions, new[] { OfficeFormats.Word }), CancellationToken.None);
            Assert.False(check.IsAccepted); Assert.Contains(fragment, check.Reason); Assert.DoesNotContain("private", check.Reason);
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }

    private static byte[] Package(params (string Name, string Content)[] parts)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var part in parts) { using var writer = new StreamWriter(zip.CreateEntry(part.Name).Open()); writer.Write(part.Content); }
        return memory.ToArray();
    }
    private static byte[] Workbook(bool macro, bool encrypted)
    {
        byte[] b = new byte[4096];
        Put16(b, 0, 0x0809); Put16(b, 2, 4); Put16(b, 4, 0x0600); Put16(b, 6, 5);
        if (encrypted) { Put16(b, 8, 0x002F); Put16(b, 10, 0); return b; }
        Put16(b, 8, 0x0085); Put16(b, 10, 8); Put32(b, 12, 24); b[17] = macro ? (byte)1 : (byte)0;
        Put16(b, 20, 0x000A); Put16(b, 22, 0);
        Put16(b, 24, 0x0809); Put16(b, 26, 4); Put16(b, 28, 0x0600); Put16(b, 30, macro ? (ushort)0x0040 : (ushort)0x0010);
        return b;
    }
    private static byte[] Compound(string name, byte[] stream)
    {
        byte[] bytes = new byte[11 * 512]; new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(bytes, 0);
        Put16(bytes, 26, 3); Put16(bytes, 28, 0xFFFE); Put16(bytes, 30, 9); Put16(bytes, 32, 6);
        Put32(bytes, 44, 1); Put32(bytes, 48, 1); Put32(bytes, 56, 4096); Put32(bytes, 60, 0xFFFFFFFE); Put32(bytes, 68, 0xFFFFFFFE);
        for (int i = 0; i < 109; i++) Put32(bytes, 76 + i * 4, i == 0 ? 0u : 0xFFFFFFFF);
        for (int i = 0; i < 128; i++) Put32(bytes, 512 + i * 4, 0xFFFFFFFF);
        Put32(bytes, 512, 0xFFFFFFFD); Put32(bytes, 516, 0xFFFFFFFE);
        for (int i = 2; i < 10; i++) Put32(bytes, 512 + i * 4, i == 9 ? 0xFFFFFFFE : (uint)(i + 1));
        void DirectoryEntry(int offset, string text, byte type, uint start, uint size)
        {
            byte[] encoded = Encoding.Unicode.GetBytes(text + "\0"); encoded.CopyTo(bytes, offset); Put16(bytes, offset + 64, (ushort)encoded.Length); bytes[offset + 66] = type;
            Put32(bytes, offset + 116, start); Put32(bytes, offset + 120, size);
        }
        DirectoryEntry(1024, "Root Entry", 5, 0xFFFFFFFE, 0); DirectoryEntry(1152, name, 2, 2, 4096);
        stream.CopyTo(bytes, 1536); return bytes;
    }
    private static void Put16(byte[] b, int offset, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(offset, 2), v);
    private static void Put32(byte[] b, int offset, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset, 4), v);
}
