namespace Claudinine.Tests;

/// <summary>
/// Wake-from-idle detection: SessionEnd leaves a `.end` marker in the colocated
/// dir, and the next start boundary consumes it — on Cowork hosts that boundary
/// is the first UserPromptSubmit after a silent re-hydration, since SessionStart
/// never fires there. The consume-side HookRunner wiring is exercised only up to
/// the marker mechanics: a consumed marker triggers the same per-user
/// housekeeping sweeps SessionStart does, which tests must not touch (see
/// HookRunnerTests).
/// </summary>
public sealed class EndMarkerTests : IDisposable
{
    private readonly string _dir;

    public EndMarkerTests()
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

    private static int Run(string stdin) =>
        HookRunner.Run(new MemoryStream(Encoding.UTF8.GetBytes(stdin)));

    private static string MarkerPathFor(string transcriptPath) =>
        Path.Combine(
            MirrorLocator.ClaudinineDirFor(transcriptPath),
            Path.GetFileNameWithoutExtension(transcriptPath) + ".end");

    [Test]
    public async Task WriteThenConsume_ReturnsTrueAndDeletes()
    {
        string transcript = Path.Combine(_dir, "abc.jsonl");
        EndMarker.Write(transcript);
        await Assert.That(File.Exists(MarkerPathFor(transcript))).IsTrue();

        await Assert.That(EndMarker.Consume(transcript)).IsTrue();
        await Assert.That(File.Exists(MarkerPathFor(transcript))).IsFalse();
    }

    [Test]
    public async Task ConsumeWithoutMarker_ReturnsFalse()
    {
        string transcript = Path.Combine(_dir, "abc.jsonl");
        await Assert.That(EndMarker.Consume(transcript)).IsFalse();
    }

    [Test]
    public async Task SecondConsume_ReturnsFalse()
    {
        string transcript = Path.Combine(_dir, "abc.jsonl");
        EndMarker.Write(transcript);
        await Assert.That(EndMarker.Consume(transcript)).IsTrue();
        await Assert.That(EndMarker.Consume(transcript)).IsFalse();
    }

    [Test]
    public async Task MarkerCarriesFormatHeader()
    {
        string transcript = Path.Combine(_dir, "abc.jsonl");
        EndMarker.Write(transcript);
        string content = File.ReadAllText(MarkerPathFor(transcript));
        await Assert.That(content).StartsWith("{\"claudinine\"");
        await Assert.That(content).Contains("endMarkerOf");
    }

    [Test]
    public async Task SessionEndHook_WritesMarker()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello").AssistantText("hi").WriteTo(_dir);

        await Assert.That(Run($$"""
            {"hook_event_name":"SessionEnd","transcript_path":"{{path.Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

        await Assert.That(File.Exists(MarkerPathFor(path))).IsTrue();
    }

    // The teardown stamp: the file at rest after the SessionEnd pass is exactly
    // what the next re-hydration loads, so SessionEnd writes the load stamp and
    // the wake path writes none (a Stop wake fires a full turn late).
    [Test]
    public async Task SessionEndHook_WritesLoadStamp()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello").AssistantText("hi").WriteTo(_dir);

        await Assert.That(Run($$"""
            {"hook_event_name":"SessionEnd","transcript_path":"{{path.Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

        string stamp = Path.Combine(
            MirrorLocator.ClaudinineDirFor(path),
            Path.GetFileNameWithoutExtension(path) + ".load");
        await Assert.That(File.Exists(stamp)).IsTrue();
    }

    [Test]
    public async Task UserPromptSubmitHook_WithoutMarker_WritesNoLoadStamp()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello").AssistantText("hi").WriteTo(_dir);

        await Assert.That(Run($$"""
            {"hook_event_name":"UserPromptSubmit","transcript_path":"{{path.Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

        string stamp = Path.Combine(
            MirrorLocator.ClaudinineDirFor(path),
            Path.GetFileNameWithoutExtension(path) + ".load");
        await Assert.That(File.Exists(stamp)).IsFalse();
    }

    // A live session's pending marker must survive the colocated sweep: the
    // sweep acts only on .jsonl/.skip/.load/.lock/.seen, and this pins that
    // contract for the .end extension.
    [Test]
    public async Task ColocatedGc_LeavesPendingMarkerAlone()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello").AssistantText("hi").WriteTo(_dir);
        EndMarker.Write(path);

        MirrorFile.CollectGarbageColocated(MirrorLocator.ClaudinineDirFor(path));

        await Assert.That(File.Exists(MarkerPathFor(path))).IsTrue();
    }
}
