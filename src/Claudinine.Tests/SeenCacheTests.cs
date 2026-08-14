namespace Claudinine.Tests;

/// <summary>
/// The mirror's seen-cache sidecar: same mirror bytes with or without it, in
/// every state the cache can be in — fresh, valid, stale, torn, deleted. The
/// invariant under test is that the cache NEVER changes what lands in the
/// mirror; it only changes how the dedup state was obtained.
/// </summary>
public sealed class SeenCacheTests : IDisposable
{
    private readonly string _dir;
    private static readonly string LongOutput = new('x', 500);

    public SeenCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "claudinine-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("CLAUDE_PLUGIN_DATA", Path.Combine(_dir, "plugin-data"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_PLUGIN_DATA", null);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private TranscriptBuilder Session(int turns = 3)
    {
        var b = new TranscriptBuilder();
        for (int i = 0; i < turns; i++)
        {
            b.UserPrompt($"prompt {i}");
            b.BashRead($"sed -n '1,100p' src/file{i}.cs", out _, LongOutput);
        }
        b.AssistantText("done");
        return b;
    }

    [Test]
    public async Task CacheIsWrittenOnFirstPassAndValidAfter()
    {
        string path = Session().WriteTo(_dir);
        Compactor.Run(path);

        string mirror = MirrorLocator.PathFor(path);
        string cache = SeenCache.PathFor(mirror);
        await Assert.That(File.Exists(cache)).IsTrue();

        // Final line is the marker for the mirror's current length.
        string[] lines = File.ReadAllLines(cache);
        await Assert.That(lines[^1]).IsEqualTo($"len:{new FileInfo(mirror).Length}");
    }

    [Test]
    public async Task SecondPassWithValidCacheLeavesMirrorIdentical()
    {
        string path = Session().WriteTo(_dir);
        Compactor.Run(path);
        string mirror = MirrorLocator.PathFor(path);
        string mirrorAfterFirst = File.ReadAllText(mirror);

        Compactor.Run(path); // cache-served no-op pass

        await Assert.That(File.ReadAllText(mirror)).IsEqualTo(mirrorAfterFirst);
    }

    [Test]
    public async Task CacheServedPassProducesSameMirrorAsFullRead()
    {
        // Two identical sessions; one keeps its cache, the other has it deleted
        // before every pass (permanent full-read). Mirrors must match line for
        // line (headers differ by path, so compare from line 1).
        string pathA = Session().WriteTo(_dir);
        string pathB = Session().WriteTo(_dir);
        Compactor.Run(pathA);
        Compactor.Run(pathB);
        SeenCache.TryDelete(MirrorLocator.PathFor(pathB));

        // Grow both transcripts the same way, then run again.
        foreach (string p in new[] { pathA, pathB })
        {
            File.AppendAllText(p,
                """{"type":"user","uuid":"late-1","message":{"role":"user","content":"more"}}""" + "\n");
            Compactor.Run(p);
        }

        string[] a = File.ReadAllLines(MirrorLocator.PathFor(pathA));
        string[] b = File.ReadAllLines(MirrorLocator.PathFor(pathB));
        await Assert.That(a[1..]).IsEquivalentTo(b[1..]);
    }

    [Test]
    public async Task OutOfBandMirrorWriteInvalidatesCache()
    {
        string path = Session().WriteTo(_dir);
        Compactor.Run(path);
        string mirror = MirrorLocator.PathFor(path);

        // Simulate a writer that knows nothing about the cache (fork adoption,
        // older version, manual edit): the length key no longer matches.
        File.AppendAllText(mirror,
            """{"type":"user","uuid":"out-of-band","message":{"role":"user","content":"x"}}""" + "\n");
        string mirrorWithForeign = File.ReadAllText(mirror);

        Compactor.Run(path); // must full-read: sees the foreign record, appends nothing twice

        // The foreign record survives exactly once, and the pass appended nothing
        // (every transcript record is already mirrored).
        await Assert.That(File.ReadAllText(mirror)).IsEqualTo(mirrorWithForeign);
        // Cache healed: valid again for the new length.
        string[] lines = File.ReadAllLines(SeenCache.PathFor(mirror));
        await Assert.That(lines[^1]).IsEqualTo($"len:{new FileInfo(mirror).Length}");
    }

    [Test]
    public async Task TornCacheFallsBackToFullRead()
    {
        string path = Session().WriteTo(_dir);
        Compactor.Run(path);
        string mirror = MirrorLocator.PathFor(path);
        string cache = SeenCache.PathFor(mirror);
        string mirrorAfterFirst = File.ReadAllText(mirror);

        // Tear the cache: drop its final line (the marker).
        string[] lines = File.ReadAllLines(cache);
        File.WriteAllLines(cache, lines[..^1]);

        var transcript = Claudinine.Transcript.TranscriptFile.TryLoad(path)!;
        await Assert.That(MirrorFile.TryAppendMissing(transcript)).IsTrue();

        await Assert.That(File.ReadAllText(mirror)).IsEqualTo(mirrorAfterFirst);
        // And the torn cache was healed by the full read.
        await Assert.That(File.ReadAllLines(cache)[^1])
            .IsEqualTo($"len:{new FileInfo(mirror).Length}");
    }

    [Test]
    public async Task GarbageCollectionRemovesCacheWithMirrorAndOrphans()
    {
        string path = Session().WriteTo(_dir);
        Compactor.Run(path);
        string mirror = MirrorLocator.PathFor(path);
        string mirrorDir = Path.GetDirectoryName(mirror)!;

        // An orphan: a sidecar whose mirror never existed.
        string orphan = Path.Combine(mirrorDir, "gone.jsonl.seen");
        File.WriteAllText(orphan, "claudinine-seen v1\nlen:0\n");

        File.Delete(path); // transcript gone → mirror is garbage
        MirrorFile.CollectGarbage([mirrorDir]);

        await Assert.That(File.Exists(mirror)).IsFalse();
        await Assert.That(File.Exists(SeenCache.PathFor(mirror))).IsFalse();
        await Assert.That(File.Exists(orphan)).IsFalse();
    }
}
