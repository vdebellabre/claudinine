namespace Claudinine.Tests;

/// <summary>
/// The opt-in debug file sink (<see cref="Dbg.FileSink"/>). Marked NotInParallel:
/// the sink is global state, and while it is pointed at a test file every
/// concurrently running test's Dbg.Log would land there too.
/// </summary>
[NotInParallel]
public sealed class DbgTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _originalSink;

    public DbgTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "claudinine-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _originalSink = Dbg.FileSink;
    }

    public void Dispose()
    {
        Dbg.FileSink = _originalSink;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Test]
    public async Task LogAppendsToTheSinkFile()
    {
        string sink = Path.Combine(_dir, "debug.log");
        File.WriteAllText(sink, "");
        Dbg.FileSink = sink;

        Dbg.Log("first message");
        Dbg.Log("second message");

        string text = File.ReadAllText(sink);
        await Assert.That(text).Contains("first message");
        await Assert.That(text).Contains("second message");
        // Each line carries the writer's pid — concurrent hook processes share
        // one file, and without it interleaved passes are unreadable.
        await Assert.That(text).Contains($"[{Environment.ProcessId}]");
    }

    [Test]
    public async Task NoSinkMeansNoWriteAndNoThrow()
    {
        Dbg.FileSink = null;
        Dbg.Log("goes nowhere");
        await Assert.That(Directory.GetFiles(_dir)).IsEmpty();
    }

    [Test]
    public async Task SinkStopsAppendingAtTheGrowthCap()
    {
        string sink = Path.Combine(_dir, "debug.log");
        // Sparse-extend past the 10 MB cap without writing 10 MB of data.
        using (var f = File.Create(sink))
            f.SetLength(11 * 1024 * 1024);
        Dbg.FileSink = sink;

        Dbg.Log("over the cap");

        await Assert.That(new FileInfo(sink).Length).IsEqualTo(11L * 1024 * 1024);
    }

    [Test]
    public async Task UnwritableSinkIsSwallowed()
    {
        string sink = Path.Combine(_dir, "locked.log");
        // Hold the file with no sharing: the append inside Log must fail, and
        // the failure must not escape — the sink never breaks the pass.
        using var holder = new FileStream(
            sink, FileMode.Create, FileAccess.Write, FileShare.None);
        Dbg.FileSink = sink;

        Dbg.Log("cannot land"); // must not throw
        await Assert.That(new FileInfo(sink).Length).IsEqualTo(0L);
    }
}
