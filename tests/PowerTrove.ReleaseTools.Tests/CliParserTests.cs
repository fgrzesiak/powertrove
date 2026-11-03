using PowerTrove.ReleaseTools.Cli;
using Xunit;

namespace PowerTrove.ReleaseTools.Tests;

public sealed class CliParserTests
{
    private static (RootCommand Root, Command Command, Option<string> Required, Option<string> Default,
        Option<int?> Number, Option<bool> Flag) Fixture(Action<string, string, int?, bool> run)
    {
        var root = new RootCommand("Test release commands");
        var required = new Option<string>("--required", "Required input.") { IsRequired = true };
        var optional = new Option<string>("--default", () => "fallback", "Default input.");
        var number = new Option<int?>("--number", "Optional numeric input.");
        var flag = new Option<bool>("--flag", "Optional boolean switch.");
        var cmd = new Command("make", "Build something")
        {
            required, optional, number, flag
        };
        cmd.SetHandler((string value, string fallback, int? n, bool f) =>
        {
            run(value, fallback, n, f);
            return Task.CompletedTask;
        }, required, optional, number, flag);
        root.AddCommand(cmd);
        return (root, cmd, required, optional, number, flag);
    }

    [Fact]
    public async Task DefaultsAndRequiredOptionAreBoundWithoutSideEffects()
    {
        string? result = null;
        string? fallback = null;
        int? number = 7;
        bool? flag = null;
        var cli = Fixture((v, d, n, b) => { result = v; fallback = d; number = n; flag = b; });
        Assert.Equal(0, await cli.Root.InvokeAsync(["make", "--required", "input"]));
        Assert.Equal("input", result);
        Assert.Equal("fallback", fallback);
        Assert.Null(number);
        Assert.False(flag);
    }

    [Fact]
    public async Task SupportsEqualsValuesNegativeIntegersAndBooleanValues()
    {
        string? result = null;
        int? number = null;
        bool flag = false;
        var cli = Fixture((v, _, n, b) => { result = v; number = n; flag = b; });
        Assert.Equal(0, await cli.Root.InvokeAsync(
            ["make", "--required=input", "--number", "-23", "--flag=true"]));
        Assert.Equal("input", result);
        Assert.Equal(-23, number);
        Assert.True(flag);
    }

    [Theory]
    [InlineData("make --required ok --unknown")]
    [InlineData("make --required ok unexpected")]
    [InlineData("missing-command")]
    public async Task UnknownOptionArgumentOrCommandReturnsNonZero(string input)
    {
        var args = input.Split(' ');
        bool called = false;
        var cli = Fixture((_, _, _, _) => called = true);
        using var stderr = new StringWriter();
        Assert.Equal(1, await cli.Root.InvokeAsync(args, error: stderr));
        Assert.Contains("Error:", stderr.ToString());
        Assert.False(called);
    }

    [Theory]
    [InlineData("make --required")]
    [InlineData("make --required hello --number")]
    [InlineData("make --required hello --number --flag")]
    public async Task MissingValueReturnsErrorWithoutInvokingHandler(string input)
    {
        var args = input.Split(' ');
        bool called = false;
        var cli = Fixture((_, _, _, _) => called = true);
        using var stderr = new StringWriter();
        Assert.Equal(1, await cli.Root.InvokeAsync(args, error: stderr));
        Assert.NotEmpty(stderr.ToString());
        Assert.False(called);
    }

    [Fact]
    public async Task MissingRequiredOptionReturnsError()
    {
        bool called = false;
        var cli = Fixture((_, _, _, _) => called = true);
        Assert.Equal(1, await cli.Root.InvokeAsync(["make"]));
        Assert.False(called);
    }

    [Fact]
    public async Task BadNumberAndRepeatedOptionReturnError()
    {
        var cli = Fixture((_, _, _, _) => { });
        Assert.Equal(1, await cli.Root.InvokeAsync(["make", "--required", "ok", "--number", "NaN"]));
        Assert.Equal(1, await cli.Root.InvokeAsync(["make", "--required", "ok", "--required", "repeat"]));
    }

    [Theory]
    [InlineData("make", "--help")]
    [InlineData("make", "-h")]
    [InlineData("make", "-?")]
    public async Task HelpIncludesCommandOptionsDescriptionsAndRequiredMarker(string cmd, string flag)
    {
        bool called = false;
        var cli = Fixture((_, _, _, _) => called = true);
        using var output = new StringWriter();
        Assert.Equal(0, await cli.Root.InvokeAsync([cmd, flag], output: output));
        Assert.Contains("Build something", output.ToString());
        Assert.Contains("--required", output.ToString());
        Assert.Contains("Required input.", output.ToString());
        Assert.Contains("--default", output.ToString());
        Assert.Contains("fallback", output.ToString());
        Assert.False(called);
    }

    [Fact]
    public async Task RootHelpListsCommandsAndCannotInvokeHandler()
    {
        bool called = false;
        var cli = Fixture((_, _, _, _) => called = true);
        using var output = new StringWriter();
        Assert.Equal(0, await cli.Root.InvokeAsync(["--help"], output: output));
        Assert.Contains("make", output.ToString());
        Assert.Contains("Test release commands", output.ToString());
        Assert.False(called);
    }

    [Theory]
    [InlineData("package")]
    [InlineData("prepare-msix")]
    [InlineData("extract-msix-payload")]
    [InlineData("verify-msix-payload")]
    [InlineData("pack-plan")]
    [InlineData("pack-bundles")]
    [InlineData("pack-github-assets")]
    [InlineData("pack-keygen")]
    [InlineData("pack-export-public")]
    [InlineData("pack-configuration")]
    [InlineData("collect-notices")]
    [InlineData("collect-pack-notices")]
    [InlineData("verify-pack-plan")]
    [InlineData("verify-candidate")]
    [InlineData("installer-build")]
    public async Task RegisteredReleaseCommandsExposeHelpWithoutExecuting(string command)
    {
        Assert.Equal(0, await Program.Main([command, "--help"]));
    }

    [Fact]
    public async Task RealReleaseCliRejectsUnknownOptionAndMissingRequiredValue()
    {
        Assert.Equal(1, await Program.Main(["package", "--unexpected"]));
        Assert.Equal(1, await Program.Main(["package", "--version"]));
        Assert.Equal(1, await Program.Main(["package"]));
    }

    [Fact]
    public async Task MissingCommandFailsAndShowsHelpOnError()
    {
        var cli = Fixture((_, _, _, _) => { });
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await cli.Root.InvokeAsync([], output, error));
        Assert.Empty(output.ToString());
        Assert.Contains("Required command was not provided.", error.ToString());
        Assert.Contains("make", error.ToString());
    }

    [Fact]
    public async Task HandlerFailureReportsTheStackTrace()
    {
        var cli = Fixture((_, _, _, _) => throw new InvalidOperationException("boom"));
        using var error = new StringWriter();
        Assert.Equal(1, await cli.Root.InvokeAsync(["make", "--required", "x"], error: error));
        Assert.Contains("Unhandled exception: System.InvalidOperationException: boom", error.ToString());
        Assert.Contains(" at ", error.ToString());
    }

    [Fact]
    public async Task HelpColumnsAlignAfterTheLongestLabel()
    {
        var root = new RootCommand("Root");
        var command = new Command("long", "Long labels")
        {
            new Option<string>("--a-very-long-option-name-for-alignment", "First."),
            new Option<string>("--short", "Second."),
        };
        root.AddCommand(command);
        using var output = new StringWriter();
        Assert.Equal(0, await root.InvokeAsync(["long", "--help"], output));
        var lines = output.ToString().Split(Environment.NewLine);
        int first = lines.Single(l => l.Contains("First.")).IndexOf("First.", StringComparison.Ordinal);
        int second = lines.Single(l => l.Contains("Second.")).IndexOf("Second.", StringComparison.Ordinal);
        Assert.Equal(first, second);
        Assert.Contains("<a-very-long-option-name-for-alignment>  First.", output.ToString());
    }
}
