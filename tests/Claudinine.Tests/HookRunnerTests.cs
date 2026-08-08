using System.Text;
using Xunit;

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

    [Fact]
    public void GarbageStdinExitsZero() =>
        Assert.Equal(0, Run("this is not json at all {"));

    [Fact]
    public void EmptyStdinExitsZero() =>
        Assert.Equal(0, Run(""));

    [Fact]
    public void MissingTranscriptPathExitsZero() =>
        Assert.Equal(0, Run("""{"hook_event_name":"UserPromptSubmit"}"""));

    [Fact]
    public void NonexistentTranscriptExitsZero() =>
        Assert.Equal(0, Run($$"""
            {"hook_event_name":"UserPromptSubmit","transcript_path":"{{Path.Combine(_dir, "gone.jsonl").Replace("\\", "\\\\")}}"}
            """));

    [Fact]
    public void UnknownEventLeavesTranscriptUntouched()
    {
        string path = new TranscriptBuilder().UserPrompt("hello").AssistantText("hi").WriteTo(_dir);
        byte[] before = File.ReadAllBytes(path);

        Assert.Equal(0, Run($$"""
            {"hook_event_name":"SomeFutureEvent","transcript_path":"{{path.Replace("\\", "\\\\")}}"}
            """));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void UserPromptSubmitCompactsAndExitsZero()
    {
        var b = new TranscriptBuilder();
        for (int i = 0; i < 8; i++)
        {
            b.UserPrompt($"look ({i})");
            b.BashRead("sed -n '1,100p' src/foo.cs", out _, new string('x', 500));
        }
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Assert.Equal(0, Run($$"""
            {"hook_event_name":"UserPromptSubmit","transcript_path":"{{path.Replace("\\", "\\\\")}}"}
            """));

        Assert.Contains("[claudinine", File.ReadAllText(path));
    }
}
