namespace ContextBar.Abstractions.Tests;

public class ActionParameterValuesTests
{
    private static readonly ActionParameterDefinition[] Definitions =
    {
        ActionParameterDefinition.Integer("Quality", "Quality", 80, 1, 100),
        ActionParameterDefinition.Boolean("Strip", "Strip", true),
        ActionParameterDefinition.Choice("Mode", "Mode", "a", new[] { new ActionParameterChoice("a", "A"), new ActionParameterChoice("b", "B") }),
        ActionParameterDefinition.Text("Pages", "Pages", "1"),
        ActionParameterDefinition.Password("Secret", "Secret"),
        ActionParameterDefinition.FilePath("Path", "Path", new[] { ".png" }),
        ActionParameterDefinition.Color("Tint", "Tint", "#FF0000")
    };

    [Fact]
    public void MissingValues_UseDefaults()
    {
        var values = ActionParameterValues.From(new EmptyParameters(), Definitions);
        Assert.Equal(80, values.GetInt32("Quality"));
        Assert.True(values.GetBoolean("Strip"));
        Assert.Equal("a", values.GetChoice("Mode"));
        Assert.Equal("1", values.GetText("Pages"));
        Assert.Equal(string.Empty, values.GetPassword("Secret"));
        Assert.Equal(string.Empty, values.GetFilePath("Path"));
        Assert.Equal((255, 0, 0), values.GetColor("Tint"));
    }

    [Fact]
    public void Values_AreValidatedAndClamped()
    {
        var values = ActionParameterValues.From(Snapshots.Parameters(("quality", "250"), ("Strip", "nonsense"), ("Mode", "B"), ("Pages", "  2-3 "), ("Secret", "p@ssword"), ("Path", " /a/b.png "), ("Tint", "#00FF00")), Definitions);
        Assert.Equal(100, values.GetInt32("Quality"));
        Assert.True(values.GetBoolean("Strip"));
        Assert.Equal("b", values.GetChoice("Mode"));
        Assert.Equal("2-3", values.GetText("Pages"));
        Assert.Equal("p@ssword", values.GetPassword("Secret"));
        Assert.Equal("/a/b.png", values.GetFilePath("Path"));
        Assert.Equal((0, 255, 0), values.GetColor("Tint"));
    }

    [Fact]
    public void UnknownChoiceAndMalformedNumbers_FallBackToDefault()
    {
        var values = ActionParameterValues.From(Snapshots.Parameters(("Quality", "abc"), ("Mode", "evil"), ("Tint", "invalid")), Definitions);
        Assert.Equal(80, values.GetInt32("Quality"));
        Assert.Equal("a", values.GetChoice("Mode"));
        Assert.Equal((255, 0, 0), values.GetColor("Tint"));
    }

    [Fact]
    public void UndeclaredParameter_Throws()
    {
        var values = ActionParameterValues.From(new EmptyParameters(), Definitions);
        Assert.Throws<InvalidOperationException>(() => values.GetInt32("Nope"));
        Assert.Throws<InvalidOperationException>(() => values.GetBoolean("Quality"));
    }

    [Fact]
    public void ChoiceDefault_MustBeAChoice()
    {
        Assert.Throws<ArgumentException>(() => ActionParameterDefinition.Choice("x", "x", "c", new[] { new ActionParameterChoice("a", "A") }));
    }

    [Fact]
    public void PasswordDefinition_HasNoDefault()
    {
        var password = ActionParameterDefinition.Password("x", "x");
        Assert.Null(password.DefaultValue);
        Assert.Equal(ActionParameterKind.Password, password.Kind);
    }
}

public sealed class OutputPlannerAndWriterTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly PhysicalOutputFileSystem _fs = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void PlanFile_RespectsReservedNamesAndDirectories()
    {
        var planner = new OutputPlanner(_fs);
        Directory.CreateDirectory(_dir.File("out.txt"));
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _dir.File("out (1).txt") };

        var plan = planner.PlanFile(_dir.Path, "out", ".txt", reserved);

        Assert.Equal(_dir.File("out (2).txt"), plan.TargetPath);
        Assert.False(plan.WillOverwrite);
    }

    [Fact]
    public void PlanFile_SanitizesInvalidCharacters()
    {
        var plan = new OutputPlanner(_fs).PlanFile(_dir.Path, "a:b*c?. ", ".txt");
        Assert.Equal(_dir.File("a_b_c_.txt"), plan.TargetPath);
    }

    [Fact]
    public void PlanDirectory_AvoidsExistingFiles()
    {
        _dir.Write("photos", "a file, not a folder");
        var plan = new OutputPlanner(_fs).PlanDirectory(_dir.Path, "photos");
        Assert.Equal(_dir.File("photos (1)"), plan.TargetPath);
    }

    [Fact]
    public async Task WriteDirectory_CommitsAtomically()
    {
        var writer = new FileOutputWriter(_fs, new OutputPlanner(_fs));

        string result = await writer.WriteDirectoryAsync(_dir.Path, "extracted", (temp, _) =>
        {
            File.WriteAllText(Path.Combine(temp, "x.txt"), "x");
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(_dir.File("extracted"), result);
        Assert.True(File.Exists(Path.Combine(result, "x.txt")));
        Assert.Single(Directory.GetDirectories(_dir.Path));
    }

    [Fact]
    public async Task WriteDirectory_FailureRemovesTemporaryFolder()
    {
        var writer = new FileOutputWriter(_fs, new OutputPlanner(_fs));

        await Assert.ThrowsAsync<FileProcessingException>(() => writer.WriteDirectoryAsync(_dir.Path, "extracted", (temp, _) =>
        {
            File.WriteAllText(Path.Combine(temp, "partial.txt"), "x");
            throw new FileProcessingException("boom");
        }, CancellationToken.None));

        Assert.Empty(Directory.GetFileSystemEntries(_dir.Path));
        Assert.Empty(writer.Outputs);
    }

    [Fact]
    public async Task WriteFile_MissingFolder_FailsClearly()
    {
        var writer = new FileOutputWriter(_fs, new OutputPlanner(_fs));
        var ex = await Assert.ThrowsAsync<FileProcessingException>(() =>
            writer.WriteFileAsync(_dir.File("missing"), "a", ".txt", (s, ct) => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("The output folder no longer exists.", ex.Message);
    }

    [Theory]
    [InlineData(new[] { "page10.pdf", "page2.pdf", "Page1.pdf" }, new[] { "Page1.pdf", "page2.pdf", "page10.pdf" })]
    [InlineData(new[] { "b.txt", "a 10.txt", "a 9.txt" }, new[] { "a 9.txt", "a 10.txt", "b.txt" })]
    [InlineData(new[] { "x007.txt", "x7.txt", "x06.txt" }, new[] { "x06.txt", "x007.txt", "x7.txt" })]
    public void NaturalSort_OrdersNumbersNumerically(string[] input, string[] expected)
    {
        Assert.Equal(expected, input.OrderBy(x => x, NaturalFileNameComparer.Instance).ToArray());
    }
}
