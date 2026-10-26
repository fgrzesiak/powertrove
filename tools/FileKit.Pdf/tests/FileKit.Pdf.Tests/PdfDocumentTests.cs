using System.Text;

namespace FileKit.Pdf.Tests;

public class PdfDocumentTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Load_ClassicXref_ReadsPagesWithInheritedAttributes()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 3, InheritedRotate = 90 }));

        Assert.Equal(3, document.PageCount);
        Assert.Equal("1.4", document.Version);
        Assert.False(document.WasRepaired);
        Assert.All(document.Pages, p => Assert.Equal(90, p.Rotate));
        Assert.NotNull(document.Pages[0].MediaBox);
        Assert.Contains("(Page 2)", TestPdfFactory.ExtractText(document, 1));
    }

    [Fact]
    public void Load_NestedPageTree_KeepsDocumentOrder()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 4, NestedPageTree = true }));

        Assert.Equal(4, document.PageCount);
        for (int i = 0; i < 4; i++)
            Assert.Contains($"(Page {i + 1})", TestPdfFactory.ExtractText(document, i));
    }

    [Fact]
    public void Load_XrefAndObjectStreams_FromRealWorldWriter()
    {
        var document = PdfDocument.Open(Fixture("linked-objstm.pdf"));

        Assert.Equal(3, document.PageCount);
        Assert.False(document.WasRepaired);
        Assert.Equal("1.5", document.Version);
        var (width, height) = PdfTools.GetPageSize(document, 0);
        Assert.InRange(width, 419, 421);   // A5: 148 mm
        Assert.InRange(height, 595, 596);  // A5: 210 mm
    }

    [Fact]
    public void Load_IncrementalUpdate_NewestObjectWins()
    {
        var original = TestPdfFactory.Create(new() { Pages = 2 });
        var updated = TestPdfFactory.AppendUpdate(original, 4, "<< /Title (Updated) >>", 10, "/Root 1 0 R /Info 4 0 R");

        var document = PdfDocument.Load(updated);

        Assert.Equal("Updated", Encoding.ASCII.GetString(((PdfString)document.Info!["Title"]!).Value));
    }

    [Fact]
    public void Load_WrongStartXref_IsRepairedByScanning()
    {
        var data = TestPdfFactory.Create(new() { Pages = 2 });
        string text = Encoding.Latin1.GetString(data);
        int marker = text.LastIndexOf("startxref", StringComparison.Ordinal);
        text = text[..marker] + "startxref\n12\n%%EOF\n";

        var document = PdfDocument.Load(Encoding.Latin1.GetBytes(text));

        Assert.True(document.WasRepaired);
        Assert.Equal(2, document.PageCount);
    }

    [Fact]
    public void Load_ShiftedObjectOffsets_AreRepaired()
    {
        var data = TestPdfFactory.Create(new() { Pages = 2 });
        // Insert bytes after the header: every xref offset is now wrong.
        var shifted = data.Take(15).Concat(Encoding.ASCII.GetBytes("% padding comment\n")).Concat(data.Skip(15)).ToArray();

        var document = PdfDocument.Load(shifted);

        Assert.Equal(2, document.PageCount);
        Assert.Contains("(Page 2)", TestPdfFactory.ExtractText(document, 1));
    }

    [Fact]
    public void Load_MissingXrefAndTrailer_IsRepaired()
    {
        var data = TestPdfFactory.Create(new() { Pages = 2 });
        string text = Encoding.Latin1.GetString(data);
        text = text[..text.IndexOf("xref", StringComparison.Ordinal)];

        var document = PdfDocument.Load(Encoding.Latin1.GetBytes(text));

        Assert.True(document.WasRepaired);
        Assert.Equal(2, document.PageCount);
    }

    [Fact]
    public void Load_Encrypted_Throws()
    {
        var data = TestPdfFactory.Create(new() { ExtraTrailerEntries = " /Encrypt << /Filter /Standard /V 1 >>" });
        Assert.Throws<PdfEncryptedException>(() => PdfDocument.Load(data));
    }

    [Fact]
    public void Load_NotPdf_ThrowsFormatException()
    {
        Assert.Throws<PdfFormatException>(() => PdfDocument.Load(Encoding.ASCII.GetBytes("hello world, definitely not a PDF")));
        Assert.Throws<PdfFormatException>(() => PdfDocument.Load(Array.Empty<byte>()));
    }

    [Fact]
    public void Load_PageTreeCycle_DoesNotHang()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 2 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>"
        };
        var document = PdfDocument.Load(TestPdfFactory.Serialize(objects, "/Root 1 0 R"));
        Assert.Equal(1, document.PageCount);
    }

    [Fact]
    public void Load_DeepNesting_HitsLimit()
    {
        string deep = new string('[', 5000) + new string(']', 5000);
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Junk {deep} >>"
        };
        Assert.ThrowsAny<PdfException>(() => PdfDocument.Load(TestPdfFactory.Serialize(objects, "/Root 1 0 R")));
    }

    [Fact]
    public void Load_SizeLimit_IsEnforced()
    {
        var data = TestPdfFactory.Create();
        Assert.Throws<PdfLimitExceededException>(() => PdfDocument.Load(data, new PdfReadLimits { MaxFileBytes = 100 }));
    }

    [Fact]
    public void Load_MutatedInputs_FailOnlyWithTypedErrors()
    {
        var seeds = new[] { TestPdfFactory.Create(new() { Pages = 3, NestedPageTree = true, LinkFirstToLast = true }), File.ReadAllBytes(Fixture("linked-objstm.pdf")) };
        var random = new Random(20261014);
        int loaded = 0;

        for (int iteration = 0; iteration < 600; iteration++)
        {
            byte[] seed = seeds[iteration % seeds.Length];
            byte[] mutated = Mutate(seed, random);
            try
            {
                var document = PdfDocument.Load(mutated);
                // Loaded documents must also survive a full rewrite.
                using var output = new MemoryStream();
                PdfTools.Merge(new[] { document }, output);
                loaded++;
            }
            catch (PdfException)
            {
            }
        }

        Assert.True(loaded > 0);
    }

    private static byte[] Mutate(byte[] seed, Random random)
    {
        var data = (byte[])seed.Clone();
        switch (random.Next(4))
        {
            case 0:
                Array.Resize(ref data, random.Next(1, data.Length));
                break;
            case 1:
                for (int k = 0; k < 1 + random.Next(16); k++) data[random.Next(data.Length)] = (byte)random.Next(256);
                break;
            case 2:
                int start = random.Next(data.Length);
                int length = Math.Min(random.Next(1, 200), data.Length - start);
                data = data.Take(start).Concat(data.Skip(start + length)).ToArray();
                break;
            default:
                string[] tokens = { "<<", ">>", "[", "]", "(", ")", "0 R", " obj", "endobj", "stream", "/Kids", "/Length 999999" };
                int at = random.Next(data.Length);
                var insert = Encoding.ASCII.GetBytes(tokens[random.Next(tokens.Length)]);
                data = data.Take(at).Concat(insert).Concat(data.Skip(at)).ToArray();
                break;
        }
        return data;
    }
}
