using PowerTrove.Platform.Windows.Shell;

namespace PowerTrove.Platform.Windows.Tests;

public class ExplorerMenuRegistrationTests
{
    private const string Executable = @"C:\Fixtures\Grüße & tools\PowerTrove.Desktop.exe";

    [Fact]
    public void RegistrationIdentity_UsesPowerTroveKeys()
    {
        Assert.Equal(@"Software\Classes\*\shell\PowerTrove.Open", ExplorerMenuRegistration.VerbPath);
        Assert.Equal("PowerTroveOwner", ExplorerMenuRegistration.OwnerValue);
        Assert.Equal("PowerTroveExecutable", ExplorerMenuRegistration.ExecutableValue);
        Assert.Equal("PowerTrove.ExplorerVerb.v1", ExplorerMenuRegistration.OwnerMarker);
    }

    [Fact]
    public void ReadingIsSideEffectFreeAndRegistrationUsesOnlyTheOwnedCurrentUserVerb()
    {
        var registry = new FakeRegistry();
        IExplorerMenuRegistration service = new ExplorerMenuRegistration(registry);
        Assert.Equal(ExplorerMenuState.NotRegistered, service.GetState(Executable));
        Assert.Equal(0, registry.Writes);
        Assert.Equal(ExplorerMenuState.Registered, service.Register(Executable, "Open in PowerTrove"));
        Assert.Equal(2, registry.Keys.Count);
        var root = registry.Keys[ExplorerMenuRegistration.VerbPath];
        Assert.Equal("Single", root["MultiSelectModel"]);
        Assert.Equal("Open in PowerTrove", root["MUIVerb"]);
        Assert.Equal(ExplorerMenuRegistration.OwnerMarker, root[ExplorerMenuRegistration.OwnerValue]);
        Assert.Equal("\"" + Executable + "\",0", root["Icon"]);
        Assert.Equal("\"" + Executable + "\" --open-selection \"%1\"", registry.Keys[ExplorerMenuRegistration.CommandPath][""]);
        Assert.DoesNotContain(registry.Keys.Keys, path => path.Contains("Run", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, registry.Notifications);
    }

    [Fact]
    public void RepeatedRegistrationCanUpdateTheLocalizedTitleAndRemoveOnlyItsOwnTree()
    {
        var registry = new FakeRegistry();
        var service = new ExplorerMenuRegistration(registry);
        registry.Keys[@"Software\Classes\*\shell\OtherApp"] = new() { [""] = "Other" };
        service.Register(Executable, "Open in PowerTrove");
        Assert.Equal(ExplorerMenuState.Registered, service.Register(Executable, "In PowerTrove öffnen"));
        Assert.Equal("In PowerTrove öffnen", registry.Keys[ExplorerMenuRegistration.VerbPath]["MUIVerb"]);
        Assert.Equal(ExplorerMenuState.NotRegistered, service.Remove(Executable));
        Assert.Single(registry.Keys);
        Assert.Contains(@"Software\Classes\*\shell\OtherApp", registry.Keys.Keys);
        int writes = registry.Writes;
        Assert.Equal(ExplorerMenuState.NotRegistered, service.Remove(Executable));
        Assert.Equal(writes, registry.Writes);
    }

    [Theory]
    [InlineData("unowned")] [InlineData("other-copy")] [InlineData("foreign-value")]
    [InlineData("foreign-child")] [InlineData("foreign-command-child")] [InlineData("delegate-command")]
    [InlineData("changed-command")] [InlineData("changed-model")] [InlineData("changed-icon")]
    public void ForeignOrAmbiguousEntriesAreNeitherOverwrittenNorDeleted(string conflict)
    {
        var registry = new FakeRegistry();
        var service = new ExplorerMenuRegistration(registry);
        service.Register(Executable, "Open");
        var root = registry.Keys[ExplorerMenuRegistration.VerbPath];
        switch (conflict)
        {
            case "unowned": root.Remove(ExplorerMenuRegistration.OwnerValue); break;
            case "other-copy": root[ExplorerMenuRegistration.ExecutableValue] = @"D:\Other\PowerTrove.Desktop.exe"; break;
            case "foreign-value": root["Foreign"] = "keep"; break;
            case "foreign-child": registry.Keys[ExplorerMenuRegistration.VerbPath + @"\foreign"] = new(); break;
            case "foreign-command-child": registry.Keys[ExplorerMenuRegistration.CommandPath + @"\foreign"] = new(); break;
            case "delegate-command": registry.Keys[ExplorerMenuRegistration.CommandPath]["DelegateExecute"] = "keep"; break;
            case "changed-command": registry.Keys[ExplorerMenuRegistration.CommandPath][""] = "other command"; break;
            case "changed-model": root["MultiSelectModel"] = "Player"; break;
            case "changed-icon": root["Icon"] = "other icon"; break;
        }
        int writes = registry.Writes;
        string before = registry.Serialize();
        Assert.Equal(ExplorerMenuState.Conflict, service.GetState(Executable));
        Assert.Equal(ExplorerMenuState.Conflict, service.Register(Executable, "Changed"));
        Assert.Equal(ExplorerMenuState.Conflict, service.Remove(Executable));
        Assert.Equal(writes, registry.Writes);
        Assert.Equal(before, registry.Serialize());
    }

    [Theory]
    [InlineData(@"\\server\share\PowerTrove.exe")]
    [InlineData(@"C:\bad%1\PowerTrove.exe")]
    [InlineData(@"C:\bad\..\PowerTrove.exe")]
    [InlineData(@"C:\bad\tool.cmd")]
    public void AmbiguousExecutablePathsNeverWriteRegistryValues(string executable)
    {
        var registry = new FakeRegistry();
        var service = new ExplorerMenuRegistration(registry);
        Assert.Equal(ExplorerMenuState.Unavailable, service.Register(executable, "Open"));
        Assert.Equal(0, registry.Writes);
    }

    [Fact]
    public void InterruptedOwnedRegistrationCanBeRepairedOrRemovedWithoutClaimingSuccess()
    {
        var registry = new FakeRegistry { FailOnWrite = 3 };
        var service = new ExplorerMenuRegistration(registry);
        Assert.Equal(ExplorerMenuState.Unavailable, service.Register(Executable, "Open"));
        Assert.Equal(ExplorerMenuState.Incomplete, service.GetState(Executable));
        registry.FailOnWrite = null;
        Assert.Equal(ExplorerMenuState.Registered, service.Register(Executable, "Open"));
        Assert.Equal(ExplorerMenuState.NotRegistered, service.Remove(Executable));
        Assert.Equal(ExplorerMenuState.Registered, service.Register(Executable, "Open"));
    }

    [Fact]
    public void PermissionFailureDoesNotExposeExceptionText()
    {
        var registry = new FakeRegistry { DenyRead = true };
        var service = new ExplorerMenuRegistration(registry);
        Assert.Equal(ExplorerMenuState.Unavailable, service.GetState(Executable));
        Assert.Equal(ExplorerMenuState.Unavailable, service.Register(Executable, "Open"));
        Assert.Equal(ExplorerMenuState.Unavailable, service.Remove(Executable));
        Assert.Equal(0, registry.Writes);
    }

    private sealed class FakeRegistry : IExplorerVerbRegistry
    {
        public Dictionary<string, Dictionary<string, string?>> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Writes { get; private set; }
        public int Notifications { get; private set; }
        public int? FailOnWrite { get; set; }
        public bool DenyRead { get; set; }
        public ExplorerVerbKey? Read(string path)
        {
            if (DenyRead) throw new UnauthorizedAccessException("private fixture details");
            if (!Keys.TryGetValue(path, out var values)) return null;
            return new ExplorerVerbKey(new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase),
                Keys.Keys.Where(key => key.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase))
                    .Select(key => key[(path.Length + 1)..].Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
        public void WriteString(string path, string name, string value)
        {
            Writes++;
            if (Writes == FailOnWrite) throw new IOException("private fixture details");
            if (!Keys.TryGetValue(path, out var values)) Keys[path] = values = new(StringComparer.OrdinalIgnoreCase);
            values[name] = value;
        }
        public void DeleteTree(string path)
        {
            Writes++;
            foreach (string key in Keys.Keys.Where(key => key.Equals(path, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)).ToArray()) Keys.Remove(key);
        }
        public void NotifyShell() => Notifications++;
        public string Serialize() => System.Text.Json.JsonSerializer.Serialize(Keys);
    }
}
