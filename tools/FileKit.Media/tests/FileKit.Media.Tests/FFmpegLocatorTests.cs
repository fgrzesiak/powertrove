using FileKit.Media;

namespace FileKit.Media.Tests;

public class FFmpegLocatorTests
{
    private static readonly string Exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
    private static readonly string App = Path.Combine("C:", "PowerTrove");
    private static readonly string Local = Path.Combine("C:", "Users", "u", "AppData", "Local");
    private static readonly string WinGet = Path.Combine(Local, "Microsoft", "WinGet");

    private static string? Find(string? path, ISet<string> files, IDictionary<string, string[]>? directories = null) =>
        FFmpegLocator.Find(App, path, Local, files.Contains,
            (root, _) => directories is not null && directories.TryGetValue(root, out var found) ? found : []);

    [Fact]
    public void AppFolder_WinsOverPathAndWinget()
    {
        string app = Path.Combine(App, Exe), onPath = Path.Combine("D:", "tools", Exe);
        Assert.Equal(app, Find("D:" + Path.DirectorySeparatorChar + "tools", new HashSet<string> { app, onPath }));
    }

    [Fact]
    public void PathEntries_AreSearchedInOrderAndQuotesAreIgnored()
    {
        string first = Path.Combine("D:", "a", Exe), second = Path.Combine("D:", "b", Exe);
        string path = string.Join(Path.PathSeparator, "", "\"" + Path.Combine("D:", "a") + "\"", Path.Combine("D:", "b"));
        Assert.Equal(first, Find(path, new HashSet<string> { first, second }));
        Assert.Equal(second, Find(path, new HashSet<string> { second }));
    }

    [Fact]
    public void WingetLinks_FindAFreshInstallationThatThePathDoesNotShowYet()
    {
        string link = Path.Combine(WinGet, "Links", Exe);
        Assert.Equal(link, Find(path: null, new HashSet<string> { link }));
    }

    [Fact]
    public void WingetPackageFolder_IsUsedWithoutLinks_NewestBuildFirst()
    {
        string packages = Path.Combine(WinGet, "Packages");
        string package = Path.Combine(packages, FFmpegLocator.WingetPackageId + "_Microsoft.Winget.Source_8wekyb3d8bbwe");
        string older = Path.Combine(package, "ffmpeg-7.1-full_build"), newer = Path.Combine(package, "ffmpeg-8.0-full_build");
        var directories = new Dictionary<string, string[]> { [packages] = [package], [package] = [older, newer] };
        var files = new HashSet<string> { Path.Combine(older, "bin", Exe), Path.Combine(newer, "bin", Exe) };
        Assert.Equal(Path.Combine(newer, "bin", Exe), Find(path: null, files, directories));
    }

    [Fact]
    public void NothingFound_ReturnsNull_AndProbeFailuresAreNotMatches()
    {
        Assert.Null(Find(path: null, new HashSet<string>()));
        Assert.Null(FFmpegLocator.Find(App, null, Local, _ => throw new UnauthorizedAccessException(), (_, _) => []));
    }
}
