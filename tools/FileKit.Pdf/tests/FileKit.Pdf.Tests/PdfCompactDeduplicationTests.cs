using System.IO.Compression;
using System.Text;

namespace FileKit.Pdf.Tests;

public class PdfCompactDeduplicationTests
{
    private static PdfWriteOptions Options(bool compact = true) => new()
    {
        Compact = compact,
        IdGenerator = () => Enumerable.Range(0, 16).Select(i => (byte)i).ToArray()
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdenticalFontProgramsFromIndependentSources_ShareOneRealObject(bool repair)
    {
        var first = FontDocument("ABCD");
        var second = FontDocument("ABCD");
        using var merged = new MemoryStream();
        PdfTools.Merge(new[] { first, second }, merged, Options(compact: !repair));
        byte[] data = merged.ToArray();
        if (repair)
        {
            using var repaired = new MemoryStream();
            PdfTools.Repair(PdfDocument.Load(data), repaired, Options());
            data = repaired.ToArray();
        }

        var result = PdfDocument.Load(data);
        Assert.Equal(2, result.PageCount);
        Assert.False(result.WasRepaired);
        AssertNoReferenceBodies(result);

        var firstProgram = FontProgram(result, 0);
        var secondProgram = FontProgram(result, 1);
        Assert.Equal(firstProgram, secondProgram);
        Assert.IsType<PdfStream>(result.GetObject(firstProgram.ObjectNumber));
        Assert.Single(Objects(result).OfType<PdfStream>().Where(s => s.Dictionary["Length1"] is PdfInteger));
        Assert.Equal("ABCD", Decode(Assert.IsType<PdfStream>(result.GetObject(firstProgram.ObjectNumber))));
        // Every level becomes equal only after its child references have been remapped.
        Assert.Equal(ResourcesReference(result, 0), ResourcesReference(result, 1));
        Assert.Single(Objects(result).OfType<PdfDictionary>().Where(d => d.GetName("Type") == "Font"));
        Assert.Single(Objects(result).OfType<PdfDictionary>().Where(d => d.GetName("Type") == "FontDescriptor"));
        var nested = Assert.IsType<PdfArray>(Resources(result, 0)["Nested"]);
        var alias = Assert.IsType<PdfReference>(Assert.IsType<PdfDictionary>(nested[0])["Alias"]);
        Assert.Equal(firstProgram, alias);
        Assert.False(result.WasRepaired); // Loading every xref entry must not trigger recovery either.

        var xref = Objects(result).OfType<PdfStream>().Single(s => s.Dictionary.GetName("Type") == "XRef");
        byte[] entries = Inflate(xref.Data);
        int size = checked((int)Assert.IsType<PdfInteger>(result.Trailer["Size"]).Value);
        Assert.Equal(size * 7, entries.Length); // /W [1 4 2]
        Assert.Contains(Enumerable.Range(1, size - 1), n => entries[n * 7] == 0);
        // Free entries must form a terminating chain; duplicates cannot be active aliases.
        var free = Enumerable.Range(1, size - 1).Where(n => entries[n * 7] == 0).ToHashSet();
        var visited = new HashSet<int>();
        int next = ReadField(entries, 1);
        while (next != 0)
        {
            Assert.Contains(next, free);
            Assert.True(visited.Add(next));
            Assert.Equal(1, entries[next * 7 + 6]);
            next = ReadField(entries, next * 7 + 1);
        }
        Assert.True(free.SetEquals(visited));
        Assert.All(Objects(result).SelectMany(References), r => Assert.DoesNotContain(r.ObjectNumber, free));
        string raw = Encoding.Latin1.GetString(data);
        Assert.All(free, n => Assert.DoesNotContain($"\n{n} 0 obj\n", raw));
        foreach (var stream in Objects(result).OfType<PdfStream>().Where(s => s.Dictionary.GetName("Type") == "ObjStm"))
        {
            byte[] decoded = Inflate(stream.Data);
            int firstOffset = checked((int)Assert.IsType<PdfInteger>(stream.Dictionary["First"]).Value);
            string[] header = Encoding.ASCII.GetString(decoded, 0, firstOffset).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < header.Length; i += 2)
                Assert.DoesNotContain(int.Parse(header[i], System.Globalization.CultureInfo.InvariantCulture), free);
        }
    }

    [Fact]
    public void OneByteDifferentFontPrograms_AndTheirParents_StayDistinct()
    {
        using var output = new MemoryStream();
        PdfTools.Merge(new[] { FontDocument("ABCD"), FontDocument("ABCE") }, output, Options());
        var result = PdfDocument.Load(output.ToArray());

        var first = FontProgram(result, 0);
        var second = FontProgram(result, 1);
        Assert.NotEqual(first, second);
        Assert.NotEqual(ResourcesReference(result, 0), ResourcesReference(result, 1));
        Assert.Equal("ABCD", Decode(Assert.IsType<PdfStream>(result.GetObject(first.ObjectNumber))));
        Assert.Equal("ABCE", Decode(Assert.IsType<PdfStream>(result.GetObject(second.ObjectNumber))));
        Assert.Equal(2, Objects(result).OfType<PdfStream>().Count(s => s.Dictionary["Length1"] is PdfInteger));
        AssertNoReferenceBodies(result);
        Assert.False(result.WasRepaired);
    }

    [Fact]
    public void RepeatedWrites_WithFixedId_AreDeterministic()
    {
        var sources = new[] { FontDocument("ABCD"), FontDocument("ABCD"), FontDocument("ABCE") };
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        PdfTools.Merge(sources, first, Options());
        PdfTools.Merge(sources, second, Options());
        Assert.Equal(first.ToArray(), second.ToArray());
    }

    [Fact]
    public void MultipleObjectStreamBatches_KeepEveryPage_WhileOmittingDuplicates()
    {
        var sources = Enumerable.Range(0, 205).Select(_ => FontDocument("ABCD")).ToArray();
        using var output = new MemoryStream();
        PdfTools.Merge(sources, output, Options());
        var result = PdfDocument.Load(output.ToArray());

        Assert.Equal(sources.Length, result.PageCount);
        Assert.All(Enumerable.Range(0, result.PageCount), i =>
        {
            Assert.Equal(FontProgram(result, 0), FontProgram(result, i));
            Assert.Equal(TestPdfFactory.ExtractText(sources[i], 0), TestPdfFactory.ExtractText(result, i));
        });
        Assert.True(Objects(result).OfType<PdfStream>().Count(s => s.Dictionary.GetName("Type") == "ObjStm") >= 2);
        AssertNoReferenceBodies(result);
        Assert.False(result.WasRepaired);
    }

    [Fact]
    public void StreamDictionaryDependencies_AreRemappedBeforeHashing()
    {
        var first = FontDocument("ABCD");
        var second = FontDocument("ABCD");
        // Metadata referenced by a stream dictionary must be canonical too.
        foreach (var source in new[] { first, second })
        {
            var font = Assert.IsType<PdfStream>(source.GetObject(FontProgram(source, 0).ObjectNumber));
            font.Dictionary["FixtureMetadata"] = new PdfReference(9, 0);
        }
        using var output = new MemoryStream();
        PdfTools.Merge(new[] { first, second }, output, Options());
        var result = PdfDocument.Load(output.ToArray());

        Assert.Equal(FontProgram(result, 0), FontProgram(result, 1));
        var program = Assert.IsType<PdfStream>(result.GetObject(FontProgram(result, 0).ObjectNumber));
        var metadata = Assert.IsType<PdfReference>(program.Dictionary["FixtureMetadata"]);
        Assert.IsType<PdfDictionary>(result.GetObject(metadata.ObjectNumber));
        Assert.Single(Objects(result).OfType<PdfDictionary>().Where(d => d["FixtureValue"] is PdfInteger));
        AssertNoReferenceBodies(result);
        Assert.False(result.WasRepaired);
    }

    [Fact]
    public void CyclicDictionaries_KeepTheirIdentity_AndStillRemapLeafReferences()
    {
        var sources = new[] { FontDocument("ABCD", cycle: true), FontDocument("ABCD", cycle: true) };
        using var output = new MemoryStream();
        PdfTools.Merge(sources, output, Options());
        var result = PdfDocument.Load(output.ToArray());

        Assert.NotEqual(ResourcesReference(result, 0), ResourcesReference(result, 1));
        Assert.Equal(FontProgram(result, 0), FontProgram(result, 1));
        for (int i = 0; i < 2; i++)
        {
            var cycleRef = Assert.IsType<PdfReference>(Resources(result, i)["Cycle"]);
            var cycle = Assert.IsType<PdfDictionary>(result.GetObject(cycleRef.ObjectNumber));
            Assert.Equal(cycleRef, Assert.IsType<PdfReference>(cycle["Self"]));
            Assert.Equal(FontProgram(result, i), Assert.IsType<PdfReference>(cycle["Program"]));
        }
        AssertNoReferenceBodies(result);
        Assert.False(result.WasRepaired);
    }

    [Fact]
    public void PdfTexFixture_ClassicAndCompact_KeepDecodedContentOnEveryPage()
    {
        var source = PdfDocument.Open(Path.Combine(AppContext.BaseDirectory, "Fixtures", "linked-objstm.pdf"));
        using var classic = new MemoryStream();
        using var compact = new MemoryStream();
        PdfTools.Merge(new[] { source }, classic, Options(compact: false));
        PdfTools.Merge(new[] { source }, compact, Options());
        var classicDocument = PdfDocument.Load(classic.ToArray());
        var compactDocument = PdfDocument.Load(compact.ToArray());

        Assert.Equal(source.PageCount, compactDocument.PageCount);
        Assert.Equal(source.PageCount, classicDocument.PageCount);
        for (int i = 0; i < source.PageCount; i++)
        {
            string expected = TestPdfFactory.ExtractText(source, i);
            Assert.Equal(expected, TestPdfFactory.ExtractText(classicDocument, i));
            Assert.Equal(expected, TestPdfFactory.ExtractText(compactDocument, i));
        }
        AssertNoReferenceBodies(classicDocument);
        AssertNoReferenceBodies(compactDocument);
        Assert.False(classicDocument.WasRepaired);
        Assert.False(compactDocument.WasRepaired);
    }

    [Fact]
    public void RepairCompact_PreservesDistinctLeavesWithAnUnrecognizedPageType()
    {
        using var source = new MemoryStream();
        new PdfDocumentBuilder().AddBlankPage(200, 300).AddBlankPage(200, 300)
            .Save(source, Options(compact: false));
        string text = Encoding.Latin1.GetString(source.ToArray());
        Assert.Contains("/Type /Page/", text);
        // Same-length corruption keeps the xref valid while removing the usual type marker.
        var document = PdfDocument.Load(Encoding.Latin1.GetBytes(text.Replace("/Type /Page/", "/Type /Poge/", StringComparison.Ordinal)));
        Assert.Equal(2, document.PageCount);
        using var output = new MemoryStream();
        PdfTools.Repair(document, output, Options());
        var repaired = PdfDocument.Load(output.ToArray());
        Assert.Equal(2, repaired.PageCount);
        Assert.NotEqual(repaired.Pages[0].Reference, repaired.Pages[1].Reference);
        Assert.False(repaired.WasRepaired);
    }

    [Fact]
    public void MutatedCompactOutputs_OnlyFailWithTypedPdfErrors()
    {
        using var seedOutput = new MemoryStream();
        PdfTools.Merge(new[] { FontDocument("ABCD"), FontDocument("ABCD") }, seedOutput, Options());
        byte[] seed = seedOutput.ToArray();
        var random = new Random(174);
        int loaded = 0;
        for (int i = 0; i < 160; i++)
        {
            byte[] mutated = (byte[])seed.Clone();
            if (i % 3 == 0) Array.Resize(ref mutated, random.Next(1, mutated.Length));
            else mutated[random.Next(mutated.Length)] ^= (byte)(1 << random.Next(8));
            try
            {
                var document = PdfDocument.Load(mutated);
                using var output = new MemoryStream();
                PdfTools.Repair(document, output, Options());
                var repaired = PdfDocument.Load(output.ToArray());
                Assert.Equal(document.PageCount, repaired.PageCount);
                Assert.False(repaired.WasRepaired);
                loaded++;
            }
            catch (PdfException) { }
        }
        Assert.True(loaded > 0);
    }

    [Fact]
    public void ClassicWriter_MatchesRecordedBytes_WithFixedId()
    {
        using var output = new MemoryStream();
        new PdfDocumentBuilder().AddBlankPage(200, 300).Save(output, Options(compact: false));
        // Literal baseline: do not construct xref offsets with the writer under test.
        const string expected = "%PDF-1.7\n%âãÏÓ\n"
            + "1 0 obj\n<</Type /Catalog/Pages 2 0 R>>\nendobj\n"
            + "2 0 obj\n<</Type /Pages/Kids [3 0 R]/Count 1>>\nendobj\n"
            + "3 0 obj\n<</Type /Page/Parent 2 0 R/MediaBox [0 0 200 300]/Resources <<>>>>\nendobj\n"
            + "4 0 obj\n<</Producer (FileKit.Pdf)>>\nendobj\n"
            + "xref\n0 5\n0000000000 65535 f\r\n"
            + "0000000015 00000 n\r\n0000000061 00000 n\r\n"
            + "0000000114 00000 n\r\n0000000196 00000 n\r\n"
            + "trailer\n<</Size 5/Root 1 0 R/ID [<000102030405060708090A0B0C0D0E0F> <000102030405060708090A0B0C0D0E0F>]/Info 4 0 R>>"
            + "\nstartxref\n239\n%%EOF\n";
        Assert.Equal(Encoding.Latin1.GetBytes(expected), output.ToArray());
    }

    // Synthetic font bytes test object identity, not font rendering. No binary fixtures needed.
    private static PdfDocument FontDocument(string program, bool cycle = false)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources 4 0 R /Contents 8 0 R >>",
            "<< /Font << /F1 5 0 R >> /Nested [<< /Alias 7 0 R >>]" + (cycle ? " /Cycle 9 0 R" : "") + " >>",
            "<< /Type /Font /Subtype /TrueType /BaseFont /Fixture /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /Fixture /FontFile2 7 0 R >>",
            TestPdfFactory.StreamObject($"/Length1 {program.Length}", program),
            TestPdfFactory.StreamObject("", "BT /F1 12 Tf (Fixture) Tj ET")
        };
        objects.Add(cycle ? "<< /Self 9 0 R /Program 7 0 R >>" : "<< /FixtureValue 42 >>");
        return PdfDocument.Load(TestPdfFactory.Serialize(objects, "/Root 1 0 R"));
    }

    private static PdfReference ResourcesReference(PdfDocument document, int page) =>
        Assert.IsType<PdfReference>(document.Pages[page].Resources);

    private static PdfDictionary Resources(PdfDocument document, int page) =>
        Assert.IsType<PdfDictionary>(document.GetObject(ResourcesReference(document, page).ObjectNumber));

    private static PdfReference FontProgram(PdfDocument document, int page)
    {
        var fonts = Assert.IsType<PdfDictionary>(Resources(document, page)["Font"]);
        var fontRef = Assert.IsType<PdfReference>(fonts["F1"]);
        var font = Assert.IsType<PdfDictionary>(document.GetObject(fontRef.ObjectNumber));
        var descriptorRef = Assert.IsType<PdfReference>(font["FontDescriptor"]);
        var descriptor = Assert.IsType<PdfDictionary>(document.GetObject(descriptorRef.ObjectNumber));
        return Assert.IsType<PdfReference>(descriptor["FontFile2"]);
    }

    private static IEnumerable<PdfObject> Objects(PdfDocument document)
    {
        int size = checked((int)Assert.IsType<PdfInteger>(document.Trailer["Size"]).Value);
        return Enumerable.Range(1, size - 1).Select(n => document.GetObject(n)!).Where(o => o is not PdfNull);
    }

    private static void AssertNoReferenceBodies(PdfDocument document)
    {
        // GetObject deliberately bypasses Resolve, which would hide reference-chain bodies.
        Assert.All(Objects(document), value => Assert.IsNotType<PdfReference>(value));
    }

    private static IEnumerable<PdfReference> References(PdfObject value) => value switch
    {
        PdfReference reference => new[] { reference },
        PdfArray array => array.Items.SelectMany(References),
        PdfDictionary dictionary => dictionary.Entries.SelectMany(e => References(e.Value)),
        PdfStream stream => References(stream.Dictionary),
        _ => Array.Empty<PdfReference>()
    };

    private static string Decode(PdfStream stream) => Encoding.Latin1.GetString(Inflate(stream.Data));

    private static byte[] Inflate(byte[] data)
    {
        using var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static int ReadField(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
}
