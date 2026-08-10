namespace Claudinine.Tests;

/// <summary>
/// The load stamp's own contract: what SessionStart writes, what the statusline
/// reads back, and GC. The statusline-level consequences live in
/// <see cref="StatuslineVerbTests"/>.
/// </summary>
public sealed class LoadStampTests : IDisposable
{
    private readonly string _dir;
    private readonly string _mirrorDir;

    public LoadStampTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "claudinine-tests", Guid.NewGuid().ToString("N"));
        _mirrorDir = Path.Combine(_dir, "plugin-data", "mirrors");
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("CLAUDE_PLUGIN_DATA", Path.Combine(_dir, "plugin-data"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_PLUGIN_DATA", null);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string NewStem() => Guid.NewGuid().ToString("N");

    [Test]
    public async Task RoundTripsRecordSizes()
    {
        string stem = NewStem();
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .BashRead("ls", out _, new string('x', 400))
            .AssistantText("done")
            .WriteTo(_dir, stem + ".jsonl");

        LoadStamp.Write(path);
        var read = LoadStamp.Read(path);

        var expected = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach ((string uuid, long size) in LoadStamp.ScanRecordSizes(path))
            expected[uuid] = size;
        await Assert.That(read).IsNotNull();
        await Assert.That(read!.Count).IsEqualTo(expected.Count);
        foreach ((string uuid, long size) in expected)
            await Assert.That(read[uuid]).IsEqualTo(size);
    }

    [Test]
    public async Task MissingTranscriptStampsAsEmpty()
    {
        // A brand-new session has no transcript when SessionStart fires; the
        // stamp must still exist and read as "loaded nothing" (empty, NOT null —
        // null means no watermark and silences the statusline for the session).
        string path = Path.Combine(_dir, NewStem() + ".jsonl");
        LoadStamp.Write(path);

        var read = LoadStamp.Read(path);
        await Assert.That(read).IsNotNull();
        await Assert.That(read!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ReadReturnsNullWithoutStamp() =>
        await Assert.That(LoadStamp.Read(Path.Combine(_dir, NewStem() + ".jsonl"))).IsNull();

    [Test]
    public async Task ReadReturnsNullOnForeignHeader()
    {
        // Format sentinel: a .load file we do not recognize must read as "no
        // stamp", not as an empty one — pricing a reload off garbage is the
        // failure mode, silence is the fallback.
        string stem = NewStem();
        Directory.CreateDirectory(_mirrorDir);
        File.WriteAllText(Path.Combine(_mirrorDir, stem + ".load"),
            "not a claudinine header\nuuid\t123\n", new UTF8Encoding(false));

        await Assert.That(LoadStamp.Read(Path.Combine(_dir, stem + ".jsonl"))).IsNull();
    }

    [Test]
    public async Task RewriteReplacesEarlierStamp()
    {
        // Every SessionStart re-stamps; the statusline must see the LAST load,
        // not the first.
        string stem = NewStem();
        string path = new TranscriptBuilder().UserPrompt("v1").WriteTo(_dir, stem + ".jsonl");
        LoadStamp.Write(path);
        var first = LoadStamp.Read(path);

        new TranscriptBuilder().UserPrompt("v1").UserPrompt("longer second prompt")
            .WriteTo(_dir, stem + ".jsonl");
        LoadStamp.Write(path);
        var second = LoadStamp.Read(path);

        await Assert.That(first!.Count).IsEqualTo(1);
        await Assert.That(second!.Count).IsEqualTo(2);
    }

    [Test]
    public async Task CollectGarbageReapsStampsOfDeadTranscripts()
    {
        string liveStem = NewStem();
        string deadStem = NewStem();
        string livePath = new TranscriptBuilder().UserPrompt("hi").WriteTo(_dir, liveStem + ".jsonl");
        string deadPath = new TranscriptBuilder().UserPrompt("bye").WriteTo(_dir, deadStem + ".jsonl");
        LoadStamp.Write(livePath);
        LoadStamp.Write(deadPath);
        File.Delete(deadPath);

        LoadStamp.CollectGarbage(_mirrorDir);

        await Assert.That(File.Exists(Path.Combine(_mirrorDir, liveStem + ".load"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(_mirrorDir, deadStem + ".load"))).IsFalse();
    }
}
