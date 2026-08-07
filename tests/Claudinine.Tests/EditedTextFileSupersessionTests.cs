using System.Text.Json.Nodes;
using Xunit;

namespace Claudinine.Tests;

/// <summary>
/// edited-text-file-supersession: a notice is removed when a LATER record gives
/// the model a full view of the same file — another notice (keep-last), a full
/// Read, or a successful Write. A surviving snippet is presented to a resumed
/// model as current content, so a stale survivor would be worse than the status
/// quo. Runs end-to-end through Compactor.Run so removal rechaining and rewrite
/// validation are exercised.
/// </summary>
public sealed class EditedTextFileSupersessionTests : IDisposable
{
    private readonly string _dir;

    public EditedTextFileSupersessionTests()
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

    /// <summary>toolUseResult of a successful Read of lines start..start+num-1 of a total-line file.</summary>
    private static JsonObject ReadResult(string filePath, int startLine, int numLines, int totalLines) =>
        new()
        {
            ["type"] = "text",
            ["file"] = new JsonObject
            {
                ["filePath"] = filePath,
                ["content"] = "content",
                ["numLines"] = numLines,
                ["startLine"] = startLine,
                ["totalLines"] = totalLines,
            },
        };

    private static JsonObject WriteResult(string filePath, string type = "update") =>
        new()
        {
            ["type"] = type,
            ["filePath"] = filePath,
            ["content"] = "written content",
            ["structuredPatch"] = new JsonArray(),
            ["originalFile"] = "old",
            ["userModified"] = false,
        };

    // ---- keep-last (supersession by a later notice) ----

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

    // ---- supersession by a later full Read ----

    [Fact]
    public void FullReadAfterNotice_RemovesEvenTheLastNotice()
    {
        string file = @"C:\proj\a.cs";
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .EditedTextFile(file, "a v1")
            .EditedTextFile(file, "a v2")
            .UserPrompt("check the file")
            .ToolCall("Read", new JsonObject { ["file_path"] = file },
                "1\ta v3", ReadResult(file, startLine: 1, numLines: 40, totalLines: 40))
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        // The whole run for that file goes: the read IS the fresher full view.
        Assert.Empty(EditedTextFiles(Load(path), file));
    }

    [Fact]
    public void PartialRead_DoesNotSupersede()
    {
        string file = @"C:\proj\a.cs";
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .EditedTextFile(file, "a v1")
            .ToolCall("Read", new JsonObject { ["file_path"] = file, ["offset"] = 60 },
                "60\tslice", ReadResult(file, startLine: 60, numLines: 27, totalLines: 86))
            .ToolCall("Read", new JsonObject { ["file_path"] = file, ["limit"] = 10 },
                "1\thead", ReadResult(file, startLine: 1, numLines: 10, totalLines: 86))
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        // Neither slice proves a full view; the notice stays the latest one.
        Assert.Single(EditedTextFiles(Load(path), file));
    }

    [Fact]
    public void FailedRead_DoesNotSupersede()
    {
        string file = @"C:\proj\a.cs";
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .EditedTextFile(file, "a v1")
            .ToolCall("Read", new JsonObject { ["file_path"] = file },
                "Error: file not found", JsonValue.Create("Error: file not found"))
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        Assert.Single(EditedTextFiles(Load(path), file));
    }

    [Fact]
    public void ReadBeforeNotice_DoesNotSupersede()
    {
        string file = @"C:\proj\a.cs";
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .ToolCall("Read", new JsonObject { ["file_path"] = file },
                "1\ta v1", ReadResult(file, startLine: 1, numLines: 40, totalLines: 40))
            .EditedTextFile(file, "a v2")
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        // File order is time order: the notice is fresher than the read.
        Assert.Single(EditedTextFiles(Load(path), file));
    }

    [Fact]
    public void FullReadOfDifferentFile_DoesNotSupersede()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .EditedTextFile(@"C:\proj\a.cs", "a v1")
            .ToolCall("Read", new JsonObject { ["file_path"] = @"C:\proj\b.cs" },
                "1\tb", ReadResult(@"C:\proj\b.cs", startLine: 1, numLines: 5, totalLines: 5))
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        Assert.Single(EditedTextFiles(Load(path), @"C:\proj\a.cs"));
    }

    // ---- supersession by a later Write; Edit never supersedes ----

    [Fact]
    public void WriteAfterNotice_Supersedes()
    {
        string file = @"C:\proj\a.cs";
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .EditedTextFile(file, "a v1")
            .ToolCall("Write", new JsonObject { ["file_path"] = file, ["content"] = "a v2" },
                "File updated successfully", WriteResult(file))
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        Assert.Empty(EditedTextFiles(Load(path), file));
    }

    [Fact]
    public void EditAfterNotice_DoesNotSupersede()
    {
        string file = @"C:\proj\a.cs";
        // Edit's toolUseResult has no "type" field: a patch builds ON the
        // notice's snapshot, so the notice stays load-bearing.
        var editResult = new JsonObject
        {
            ["filePath"] = file,
            ["oldString"] = "a",
            ["newString"] = "b",
            ["originalFile"] = "a v1",
            ["structuredPatch"] = new JsonArray(),
            ["userModified"] = false,
            ["replaceAll"] = false,
        };
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .EditedTextFile(file, "a v1")
            .ToolCall("Edit", new JsonObject { ["file_path"] = file, ["old_string"] = "a", ["new_string"] = "b" },
                "File updated successfully", editResult)
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        Assert.Single(EditedTextFiles(Load(path), file));
    }

    [Fact]
    public void WriteShapedResultFromUnknownTool_DoesNotSupersede()
    {
        string file = @"C:\proj\a.cs";
        // The result shape alone is not enough: the tool name must match too
        // (fail-closed against shape drift or a tool we never analyzed).
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .EditedTextFile(file, "a v1")
            .ToolCall("SomeNewTool", new JsonObject { ["file_path"] = file },
                "ok", WriteResult(file))
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        Assert.Single(EditedTextFiles(Load(path), file));
    }

    // ---- pass discipline ----

    [Fact]
    public void IdempotentSecondPass()
    {
        string file = @"C:\proj\a.cs";
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .EditedTextFile(file, "a v1")
            .EditedTextFile(@"C:\proj\b.cs", "b v1")
            .EditedTextFile(file, "a v2")
            .ToolCall("Read", new JsonObject { ["file_path"] = file },
                "1\ta v3", ReadResult(file, startLine: 1, numLines: 40, totalLines: 40))
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);
        byte[] afterFirst = File.ReadAllBytes(path);
        Compactor.Run(path);
        byte[] afterSecond = File.ReadAllBytes(path);

        Assert.Equal(afterFirst, afterSecond);
    }
}
