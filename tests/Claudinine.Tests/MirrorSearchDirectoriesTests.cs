using Claudinine.Mirror;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

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

    [Test]
    public async Task EnvVarDirComesFirstThenDataDirsThenFallback()
    {
        string envDir = CreateMirrorsDir("env-data");
        string inline_ = CreateMirrorsDir(".claude", "plugins", "data", "claudinine-inline");
        string cli = CreateMirrorsDir(".claude", "plugins", "data", "claudinine-claudinine");
        string fallback = CreateMirrorsDir(".claudinine");

        var dirs = MirrorLocator.SearchDirectories(Path.Combine(_home, "env-data"), _home);

        await Assert.That(dirs[0]).IsEqualTo(envDir);
        await Assert.That(dirs[^1]).IsEqualTo(fallback);
        await Assert.That(dirs.Count).IsEqualTo(4);
        await Assert.That(dirs).Contains(inline_);
        await Assert.That(dirs).Contains(cli);
    }

    [Test]
    public async Task SkipsOtherPluginsDataDirs()
    {
        CreateMirrorsDir(".claude", "plugins", "data", "cozempic-inline");
        CreateMirrorsDir(".claude", "plugins", "data", "playwright-inline");
        string ours = CreateMirrorsDir(".claude", "plugins", "data", "claudinine-inline");

        var dirs = MirrorLocator.SearchDirectories(null, _home);

        await Assert.That(dirs).IsEquivalentTo([ours]);
    }

    [Test]
    public async Task DeduplicatesEnvVarPointingIntoDataRoot()
    {
        string inline_ = CreateMirrorsDir(".claude", "plugins", "data", "claudinine-inline");

        var dirs = MirrorLocator.SearchDirectories(
            Path.Combine(_home, ".claude", "plugins", "data", "claudinine-inline"), _home);

        await Assert.That(dirs).IsEquivalentTo([inline_]);
    }

    [Test]
    public async Task SkipsMissingDirectories()
    {
        var dirs = MirrorLocator.SearchDirectories(Path.Combine(_home, "nope"), _home);

        await Assert.That(dirs).IsEmpty();
    }
}
