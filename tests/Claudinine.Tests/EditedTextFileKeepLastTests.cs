using System.Text.Json.Nodes;
using Xunit;

namespace Claudinine.Tests;

/// <summary>
/// edited-text-file-keep-last: per filename only the LAST snippet survives (it is
/// presented to a resumed model as current content, so a stale survivor would be
/// worse than the status quo). Runs end-to-end through Compactor.Run so removal
/// rechaining and rewrite validation are exercised.
/// </summary>
public sealed class EditedTextFileKeepLastTests : IDisposable
{
    private readonly string _dir;

    public EditedTextFileKeepLastTests()
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

    private static JsonObject[] Load(string path) =>
        File.ReadAllLines(path).Where(l => l.Length > 0)
            .Select(l => (JsonObject)JsonNode.Parse(l)!).ToArray();

    private static JsonObject[] EditedTextFiles(JsonObject[] records, string filename) =>
        records.Where(r => r["type"]?.GetValue<string>() == "attachment"
            && r["attachment"]?["type"]?.GetValue<string>() == "edited_text_file"
            && r["attachment"]?["filename"]?.GetValue<string>() == filename).ToArray();

    [Fact]
    public void MultiFileInterleaving_KeepsExactlyTheLastPerFile()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .EditedTextFile(@"C:\proj\a.cs", "a v1")
            .EditedTextFile(@"C:\proj\b.cs", "b v1")
            .AssistantText("mid")
            .EditedTextFile(@"C:\proj\a.cs", "a v2")
            .EditedTextFile(@"C:\proj\b.cs", "b v2")
            .EditedTextFile(@"C:\proj\a.cs", "a v3")
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        // Keep-last invariant: the survivor is the LAST snippet, never an earlier one.
        Assert.Equal("a v3", Assert.Single(EditedTextFiles(records, @"C:\proj\a.cs"))["attachment"]!["snippet"]!.GetValue<string>());
        Assert.Equal("b v2", Assert.Single(EditedTextFiles(records, @"C:\proj\b.cs"))["attachment"]!["snippet"]!.GetValue<string>());
    }

    [Fact]
    public void RemovedAttachment_ChildRechainedToSurvivingAncestor()
    {
        var b = new TranscriptBuilder()
            .UserPrompt("hello")
            .AssistantText("turn one");
        string assistantUuid = b.LastUuid!;
        b.EditedTextFile(@"C:\proj\a.cs", "a v1")
            .UserPrompt("second prompt")
            .EditedTextFile(@"C:\proj\a.cs", "a v2")
            .AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        // The second prompt was parented to the removed v1 record: re-parents to
        // the surviving assistant record before it.
        JsonObject secondPrompt = records.Single(r =>
            r["message"]?["content"] is JsonValue v
            && v.TryGetValue<string>(out string? s) && s == "second prompt");
        Assert.Equal(assistantUuid, secondPrompt["parentUuid"]!.GetValue<string>());
    }

    [Fact]
    public void LastOccurrenceAtTail_EarlierRemovedTailIntact()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .EditedTextFile(@"C:\proj\a.cs", "a v1")
            .AssistantText("done")
            .EditedTextFile(@"C:\proj\a.cs", "a v2")
            .WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        JsonObject survivor = Assert.Single(EditedTextFiles(records, @"C:\proj\a.cs"));
        Assert.Equal("a v2", survivor["attachment"]!["snippet"]!.GetValue<string>());
        Assert.Same(records[^1], survivor);
    }

    [Fact]
    public void SingleOccurrenceUntouched()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .EditedTextFile(@"C:\proj\a.cs", "a v1")
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        Assert.Single(EditedTextFiles(Load(path), @"C:\proj\a.cs"));
    }

    [Fact]
    public void OtherAttachmentTypesUntouched()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .Attachment("task_reminder", ("content", "reminder one"))
            .Attachment("task_reminder", ("content", "reminder two"))
            .Attachment("edited_text_file", ("snippet", "no filename — malformed, keep"))
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        Assert.Equal(3, Load(path).Count(r => r["type"]?.GetValue<string>() == "attachment"));
    }

    [Fact]
    public void IdempotentSecondPass()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .EditedTextFile(@"C:\proj\a.cs", "a v1")
            .EditedTextFile(@"C:\proj\b.cs", "b v1")
            .EditedTextFile(@"C:\proj\a.cs", "a v2")
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);
        byte[] afterFirst = File.ReadAllBytes(path);
        Compactor.Run(path);
        byte[] afterSecond = File.ReadAllBytes(path);

        Assert.Equal(afterFirst, afterSecond);
    }
}
