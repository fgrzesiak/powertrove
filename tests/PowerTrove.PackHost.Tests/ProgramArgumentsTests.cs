namespace PowerTrove.PackHost.Tests;

public sealed class ProgramArgumentsTests
{
    [Fact]
    public void RepeatedPackDirectoriesAndExplicitLanguageAreAccepted()
    {
        var options = Program.ParseArguments(["--pack-dir", "first", "--pack-dir", "second", "--parent-pid", "42", "--ui-language", "de"]);
        Assert.NotNull(options);
        Assert.Equal(new[] { "first", "second" }, options.PackDirectories);
        Assert.Equal(42, options.ParentPid);
        Assert.Equal("de", options.Language);
    }

    [Fact]
    public void InvalidOrAmbiguousArgumentsAreRejected()
    {
        string[][] cases = [[], ["--pack-dir", "first"], ["--parent-pid", "42"],
            ["--pack-dir", "first", "--parent-pid", "0"],
            ["--pack-dir", "first", "--parent-pid", "42", "--parent-pid", "43"],
            ["--pack-dir", "first", "--parent-pid", "42", "--ui-language", "fr"],
            ["--pack-dir", "first", "--parent-pid", "42", "--unknown", "value"],
            ["--pack-dir", "first", "--parent-pid"]];
        foreach (var args in cases) Assert.Null(Program.ParseArguments(args));
    }

    [Fact]
    public void ManifestPinsAreBoundedAndCannotBeMalformedOrDuplicated()
    {
        string hash = new('a', 64);
        var options = Program.ParseArguments(["--pack-dir", "first", "--parent-pid", "42", "--manifest-sha256", hash]);
        Assert.Equal(hash, Assert.Single(options!.ManifestHashes));
        Assert.Null(Program.ParseArguments(["--pack-dir", "first", "--parent-pid", "42", "--manifest-sha256", "invalid"]));
        Assert.Null(Program.ParseArguments(["--pack-dir", "first", "--parent-pid", "42", "--manifest-sha256", hash, "--manifest-sha256", hash]));
    }
}
