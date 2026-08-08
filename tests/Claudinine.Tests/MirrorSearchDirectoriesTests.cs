using Claudinine.Mirror;
using Xunit;

namespace Claudinine.Tests;

public sealed class MirrorSearchDirectoriesTests : IDisposable
{
    private readonly string _home;

    public MirrorSearchDirectoriesTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "claudinine-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    private string CreateMirrorsDir(params string[] segments)
    {
        string dir = Path.Combine([_home, .. segments, "mirrors"]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void EnvVarDirComesFirstThenDataDirsThenFallback()
    {
        string envDir = CreateMirrorsDir("env-data");
        string inline_ = CreateMirrorsDir(".claude", "plugins", "data", "claudinine-inline");
        string cli = CreateMirrorsDir(".claude", "plugins", "data", "claudinine-claudinine");
        string fallback = CreateMirrorsDir(".claudinine");

        var dirs = MirrorFile.SearchDirectories(Path.Combine(_home, "env-data"), _home);

        Assert.Equal(envDir, dirs[0]);
        Assert.Equal(fallback, dirs[^1]);
        Assert.Equal(4, dirs.Count);
        Assert.Contains(inline_, dirs);
        Assert.Contains(cli, dirs);
    }

    [Fact]
    public void SkipsOtherPluginsDataDirs()
    {
        CreateMirrorsDir(".claude", "plugins", "data", "cozempic-inline");
        CreateMirrorsDir(".claude", "plugins", "data", "playwright-inline");
        string ours = CreateMirrorsDir(".claude", "plugins", "data", "claudinine-inline");

        var dirs = MirrorFile.SearchDirectories(null, _home);

        Assert.Equal([ours], dirs);
    }

    [Fact]
    public void DeduplicatesEnvVarPointingIntoDataRoot()
    {
        string inline_ = CreateMirrorsDir(".claude", "plugins", "data", "claudinine-inline");

        var dirs = MirrorFile.SearchDirectories(
            Path.Combine(_home, ".claude", "plugins", "data", "claudinine-inline"), _home);

        Assert.Equal([inline_], dirs);
    }

    [Fact]
    public void SkipsMissingDirectories()
    {
        var dirs = MirrorFile.SearchDirectories(Path.Combine(_home, "nope"), _home);

        Assert.Empty(dirs);
    }
}
