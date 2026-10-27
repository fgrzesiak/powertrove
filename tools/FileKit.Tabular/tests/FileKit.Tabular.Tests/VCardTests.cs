using System.Text;

namespace FileKit.Tabular.Tests;

public sealed class VCardTests
{
    private const string Contact = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Zoë Müller\r\nN:Müller;Zoë;;;\r\nTEL;TYPE=CELL:+49 123\r\nTEL;TYPE=WORK:555\r\nEMAIL;TYPE=HOME:one@example.test\r\nEMAIL;TYPE=WORK:two@example.test\r\nNOTE:line one\\nline two\\; comma\\, slash\\\\\r\nADR;TYPE=HOME:;;Street;City;;12345;Country\r\nX-CUSTOM:preserve-on-merge\r\nEND:VCARD\r\n";
    [Fact]
    public void CommonFieldsMultipleValuesAndEscapesRoundTripThroughTheCanonicalTable()
    {
        var cards = VCard.Parse(Contact); var table = VCard.ToTable(cards);
        Assert.Contains("Phone 2", table.Headers!); Assert.Contains("Email 2", table.Headers!);
        Assert.Equal(new TextCell("+49 123"), table.Rows[0][table.Headers!.ToList().IndexOf("Phone 1")]);
        var imported = VCard.FromTable(table); var projected = VCard.ToTable(imported);
        Assert.Equal(table.Headers, projected.Headers); Assert.Equal(table.Rows[0], projected.Rows[0]);
        Assert.Contains("X-CUSTOM:preserve-on-merge", VCard.Write(cards));
        Assert.Contains("FN:Zoë Müller", VCard.Write(imported));
        Assert.Contains("NOTE:line one\\nline two\\; comma\\, slash\\\\", VCard.Write(imported));
    }
    [Fact]
    public void FoldingRespectsUtf8BytesAndUnfoldsWithoutSplittingUnicode()
    {
        var cards = VCard.Parse("BEGIN:VCARD\nVERSION:4.0\nFN:Long\nNOTE:" + new string('ü', 160) + "👋\nEND:VCARD\n");
        string output = VCard.Write(cards);
        Assert.All(output.Split("\r\n"), line => Assert.True(Encoding.UTF8.GetByteCount(line) <= 75));
        Assert.Equal(cards[0].Properties, VCard.Parse(output)[0].Properties);
        var folded = VCard.Parse("BEGIN:VCARD\nVERSION:3.0\nFN:Zoë\n  Müller\nEND:VCARD\n");
        Assert.Equal("Zoë Müller", folded[0].Properties.Single(p => p.Name == "FN").Value);
    }
    [Fact]
    public void KnownGoogleAndOutlookColumnSpellingsMapWithoutClaimingVendorCompatibility()
    {
        var google = CsvReader.Parse("Given Name,Family Name,E-mail 1 - Value,Phone 1 - Type,Phone 1 - Value\nAda,Example,ada@example.test,CELL,+123\n");
        var card = Assert.Single(VCard.FromTable(google));
        Assert.Contains(card.Properties, p => p.Name == "EMAIL" && p.Value == "ada@example.test");
        var outlook = CsvReader.Parse("First Name,Last Name,E-mail Address,Mobile Phone\nAda,Example,ada@example.test,+123\n");
        Assert.Contains(Assert.Single(VCard.FromTable(outlook)).Properties, p => p.Name == "TEL" && p.Parameters == ";TYPE=CELL");
    }
    [Theory]
    [InlineData("BEGIN:VCARD\nVERSION:2.1\nFN:Name\nEND:VCARD")]
    [InlineData("BEGIN:VCARD\nVERSION:3.0\nFN;ENCODING=QUOTED-PRINTABLE:Name\nEND:VCARD")]
    [InlineData("BEGIN:VCARD\nVERSION:3.0\nFN:Name")]
    [InlineData("BEGIN:VCARD\nVERSION:3.0\nFN:A\nFN:B\nEND:VCARD")]
    [InlineData("BEGIN:VCARD\nVERSION:3.0\nBEGIN:VCARD\nEND:VCARD")]
    public void MalformedOrUnsupportedCardsFailExplicitly(string text)
    { Assert.Throws<TabularException>(() => VCard.Parse(text)); }
    [Fact]
    public async Task UnknownColumnsInvalidUtf8AndCancellationFailWithoutDroppingData()
    {
        Assert.Throws<TabularException>(() => VCard.FromTable(CsvReader.Parse("Name,Unknown\nName,keep\n")));
        using var input = new MemoryStream([0xff]); await Assert.ThrowsAsync<TabularException>(() => VCard.ReadAsync(input));
        Assert.Throws<OperationCanceledException>(() => VCard.Parse(Contact, new(true)));
        Assert.Throws<TabularException>(() => VCard.FromTable(CsvReader.Parse("Name,Full Name\nOne,Two\n")));
    }
    [Fact]
    public void SparseRepeatedFieldsArePreservedAndRaggedOrExcessiveIndicesAreRejected()
    {
        var card = Assert.Single(VCard.FromTable(CsvReader.Parse("Full Name,Phone 101\nContact,+123\n")));
        Assert.Contains(card.Properties, p => p.Name == "TEL" && p.Value == "+123");
        Assert.Throws<TabularException>(() => VCard.FromTable(new(["Full Name", "Phone 1"], [[new TextCell("Contact")]])));
        Assert.Throws<TabularException>(() => VCard.FromTable(CsvReader.Parse("Full Name,Phone 999999\nContact,+123\n")));
    }
}
