using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Claudinine.Tests;

/// <summary>
/// The layer's core safety promise: a hook must NEVER break the session it runs
/// in — anything unexpected exits 0 silently and leaves the transcript alone.
/// SessionStart is deliberately not exercised end-to-end here: its housekeeping
/// sweeps real per-user directories that tests must not touch.
/// </summary>
public sealed class HookRunnerTests : IDisposable
{
    private readonly string _dir;

    public HookRunnerTests()
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

    [Test]
    public async Task GarbageStdinExitsZero() =>
        await Assert.That(Run("this is not json at all {")).IsEqualTo(0);

    [Test]
    public async Task EmptyStdinExitsZero() =>
        await Assert.That(Run("")).IsEqualTo(0);

    [Test]
    public async Task MissingTranscriptPathExitsZero() =>
        await Assert.That(Run("""{"hook_event_name":"UserPromptSubmit"}""")).IsEqualTo(0);

    [Test]
    public async Task NonexistentTranscriptExitsZero() =>
        await Assert.That(Run($$"""
            {"hook_event_name":"UserPromptSubmit","transcript_path":"{{Path.Combine(_dir, "gone.jsonl").Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

    [Test]
    public async Task UnknownEventLeavesTranscriptUntouched()
    {
        string path = new TranscriptBuilder().UserPrompt("hello").AssistantText("hi").WriteTo(_dir);
        byte[] before = File.ReadAllBytes(path);

        await Assert.That(Run($$"""
            {"hook_event_name":"SomeFutureEvent","transcript_path":"{{path.Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

        await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(before);
    }

    [Test]
    public async Task UserPromptSubmitCompactsAndExitsZero()
    {
        var b = new TranscriptBuilder();
        for (int i = 0; i < 8; i++)
        {
            b.UserPrompt($"look ({i})");
            b.BashRead("sed -n '1,100p' src/foo.cs", out _, new string('x', 500));
        }
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        await Assert.That(Run($$"""
            {"hook_event_name":"UserPromptSubmit","transcript_path":"{{path.Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(path)).Contains("[claudinine");
    }
}
