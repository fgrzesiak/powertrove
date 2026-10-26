using System.Xml.Linq;

namespace PowerTrove.Architecture.Tests;

/// <summary>
/// Enforces the package boundaries described in docs/architecture/action-packs-and-tools.md.
/// These rules keep tools extractable into their own repositories and packs independent
/// of the host. They read project files only and run on any OS.
/// </summary>
public class DependencyRulesTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void PackGraph_EnforcesAdapterCompositionAndHostBoundaries()
    {
        foreach (string project in AllProjects())
            foreach (string reference in ProjectReferences(project))
                Assert.True(PackDependencyRules.Allows(Relative(project), Relative(reference)),
                    $"{Relative(project)} must not reference {Relative(reference)}.");
    }

    [Theory]
    [InlineData("packs/Images/adapters/Wic/Wic.csproj", "packs/Media/adapters/Native/Native.csproj")]
    [InlineData("packs/Images/adapters/Wic/Wic.csproj", "packs/Pdf/src/Pdf/Pdf.csproj")]
    [InlineData("packs/Images/adapters/Wic/Wic.csproj", "packs/Images/adapters/Magick/Magick.csproj")]
    [InlineData("packs/Images/composition/Factory/Factory.csproj", "packs/Pdf/adapters/Native/Native.csproj")]
    [InlineData("src/PowerTrove.Platform.Windows/Host.csproj", "packs/Images/src/Images/Images.csproj")]
    [InlineData("src/PowerTrove.Infrastructure/Host.csproj", "packs/Images/adapters/Magick/Magick.csproj")]
    [InlineData("src/PowerTrove.Desktop/Host.csproj", "packs/Images/adapters/Wic/Wic.csproj")]
    public void WrongReferencesAreRejectedByTheRuleHelper(string source, string target) =>
        Assert.False(PackDependencyRules.Allows(source, target));

    [Theory]
    [InlineData("packs/Images/adapters/Wic/Wic.csproj", "packs/Images/src/Images/Images.csproj")]
    [InlineData("packs/Images/composition/Factory/Factory.csproj", "packs/Images/adapters/Wic/Wic.csproj")]
    [InlineData("src/PowerTrove.Desktop/Host.csproj", "packs/Images/composition/Factory/Factory.csproj")]
    [InlineData("packs/Images/adapters/Wic/Wic.csproj", "src/PowerTrove.Abstractions/PowerTrove.Abstractions.csproj")]
    public void IntendedReferencesAreAcceptedByTheRuleHelper(string source, string target) =>
        Assert.True(PackDependencyRules.Allows(source, target));

    public static TheoryData<string> ToolProjects => Projects("tools");
    public static TheoryData<string> PackProductionProjects => new(AllProjects().Where(p => IsUnder(p, "packs") && IsUnder(p, "src", anywhere: true)));

    [Theory]
    [MemberData(nameof(ToolProjects))]
    public void Tools_OnlyReferenceProjectsInsideTheirOwnFolder(string project)
    {
        string toolFolder = ToolFolder(project);
        foreach (var reference in ProjectReferences(project))
        {
            Assert.True(reference.StartsWith(toolFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                $"{Relative(project)} references {Relative(reference)}, outside of {Relative(toolFolder)}. Tools must stay extractable.");
        }
    }

    [Theory]
    [MemberData(nameof(ToolProjects))]
    public void Tools_ShipWithoutPackageDependencies(string project)
    {
        if (IsTestProject(project)) return;
        Assert.Empty(PackageReferences(project));
        Assert.DoesNotContain("PowerTrove", File.ReadAllText(project), StringComparison.Ordinal);
    }

    [Fact]
    public void Tools_AreSelfContainedFolders()
    {
        foreach (var tool in Directory.GetDirectories(Path.Combine(Root, "tools")))
        {
            string name = Path.GetFileName(tool);
            Assert.True(File.Exists(Path.Combine(tool, "Directory.Build.props")), $"{name} needs its own Directory.Build.props.");
            Assert.True(File.Exists(Path.Combine(tool, "README.md")), $"{name} needs a README.md.");
            Assert.True(File.Exists(Path.Combine(tool, "CHANGELOG.md")), $"{name} needs a CHANGELOG.md.");
            Assert.True(File.Exists(Path.Combine(tool, name + ".slnx")), $"{name} needs its own solution file.");
            Assert.True(Directory.Exists(Path.Combine(tool, "tests")), $"{name} needs tests.");
        }
    }

    [Theory]
    [MemberData(nameof(PackProductionProjects))]
    public void Packs_ReferenceOnlyTheSdkAndTools(string project)
    {
        Assert.Empty(PackageReferences(project));
        foreach (var reference in ProjectReferences(project))
        {
            bool isSdk = reference.EndsWith(Path.Combine("src", "PowerTrove.Abstractions", "PowerTrove.Abstractions.csproj"), StringComparison.OrdinalIgnoreCase);
            bool isTool = IsUnder(reference, "tools");
            Assert.True(isSdk || isTool, $"{Relative(project)} references {Relative(reference)}. Packs may only use PowerTrove.Abstractions and FileKit tools.");
        }
    }

    [Fact]
    public void Abstractions_HasNoDependencies()
    {
        string project = Path.Combine(Root, "src", "PowerTrove.Abstractions", "PowerTrove.Abstractions.csproj");
        Assert.Empty(ProjectReferences(project));
        Assert.Empty(PackageReferences(project));
    }

    [Fact]
    public void Core_DependsOnlyOnAbstractions()
    {
        string project = Path.Combine(Root, "src", "PowerTrove.Core", "PowerTrove.Core.csproj");
        Assert.Empty(PackageReferences(project));
        Assert.All(ProjectReferences(project), r => Assert.EndsWith("PowerTrove.Abstractions.csproj", r));
    }

    [Fact]
    public void Application_DoesNotDependOnEnginesOrPlatform()
    {
        string project = Path.Combine(Root, "src", "PowerTrove.Application", "PowerTrove.Application.csproj");
        Assert.DoesNotContain(ProjectReferences(project), r =>
            r.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("Platform.Windows", StringComparison.OrdinalIgnoreCase) ||
            IsUnder(r, "packs"));
    }

    [Fact]
    public void EveryProject_IsPartOfTheSolution()
    {
        string solution = File.ReadAllText(Path.Combine(Root, "PowerTrove.slnx")).Replace('\\', '/');
        foreach (var project in AllProjects())
        {
            string relative = Relative(project).Replace('\\', '/');
            Assert.True(solution.Contains(relative, StringComparison.OrdinalIgnoreCase), $"{relative} is missing from PowerTrove.slnx.");
        }
    }

    [Fact]
    public void ToolSolutions_ListAllProjectsOfTheTool()
    {
        foreach (var tool in Directory.GetDirectories(Path.Combine(Root, "tools")))
        {
            string solution = File.ReadAllText(Path.Combine(tool, Path.GetFileName(tool) + ".slnx")).Replace('\\', '/');
            foreach (var project in Directory.GetFiles(tool, "*.csproj", SearchOption.AllDirectories).Where(p => !IsBuildOutput(p)))
            {
                string relative = Path.GetRelativePath(tool, project).Replace('\\', '/');
                Assert.True(solution.Contains(relative, StringComparison.OrdinalIgnoreCase), $"{relative} is missing from {Path.GetFileName(tool)}.slnx.");
            }
        }
    }

    private static TheoryData<string> Projects(string topFolder) =>
        new(AllProjects().Where(p => IsUnder(p, topFolder)));

    private static IEnumerable<string> AllProjects() =>
        new[] { "src", "tests", "packs", "tools" }
            .Select(folder => Path.Combine(Root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.GetFiles(folder, "*.csproj", SearchOption.AllDirectories))
            .Where(p => !IsBuildOutput(p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

    private static bool IsBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj");

    private static bool IsTestProject(string project) => Path.GetFileNameWithoutExtension(project).EndsWith(".Tests", StringComparison.Ordinal);

    private static IReadOnlyList<string> ProjectReferences(string project)
    {
        var document = XDocument.Load(project);
        string directory = Path.GetDirectoryName(project)!;
        return document.Descendants("ProjectReference")
            .Select(e => e.Attribute("Include")!.Value.Replace('\\', Path.DirectorySeparatorChar))
            .Select(include => Path.GetFullPath(Path.Combine(directory, include)))
            .ToList();
    }

    private static IReadOnlyList<string> PackageReferences(string project) =>
        XDocument.Load(project).Descendants("PackageReference").Select(e => e.Attribute("Include")!.Value).ToList();

    private static string ToolFolder(string project)
    {
        string relative = Path.GetRelativePath(Path.Combine(Root, "tools"), project);
        return Path.Combine(Root, "tools", relative.Split(Path.DirectorySeparatorChar)[0]);
    }

    private static bool IsUnder(string path, string folder, bool anywhere = false)
    {
        string relative = Path.GetRelativePath(Root, path);
        var segments = relative.Split(Path.DirectorySeparatorChar);
        return anywhere ? segments.Contains(folder, StringComparer.OrdinalIgnoreCase) : string.Equals(segments[0], folder, StringComparison.OrdinalIgnoreCase);
    }

    private static string Relative(string path) => Path.GetRelativePath(Root, path);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PowerTrove.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
