using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Claudinine.Rules;
using Xunit;

namespace Claudinine.Tests;

/// <summary>
/// The base64-media retrieval loop: image-strip stubs old pasted images, base64
/// documents, and nested tool_result screenshots with a pointer at
/// `claudinine get &lt;sid&gt; --ref &lt;uuid&gt; --media`, and GetVerb decodes the
/// mirrored block back to a file the Read tool can view.
/// </summary>
public sealed class MediaRetrievalTests : IDisposable
{
    private readonly string _dir;

    /// <summary>Recognizable non-trivial bytes for decode round-trip asserts.</summary>
    private static readonly byte[] Payload =
        [.. Enumerable.Range(0, 2048).Select(i => (byte)(i % 251))];

    public MediaRetrievalTests()
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

    private static TranscriptBuilder AgeBy(TranscriptBuilder b, int turns)
    {
        for (int i = 0; i < turns; i++)
            b.UserPrompt($"turn filler {i}").AssistantText("ok");
        return b;
    }

    private static string RunGet(string[] args, out int rc)
    {
        var sw = new StringWriter();
        TextWriter orig = Console.Out;
        Console.SetOut(sw);
        try { rc = GetVerb.Run(args); }
        finally { Console.SetOut(orig); }
        return sw.ToString();
    }

    /// <summary>All content-block texts of every record, stub texts included.</summary>
    private static IEnumerable<string> BlockTexts(JsonObject[] records) =>
        records.SelectMany(r => (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(b => b["text"]?.GetValue<string>())
            .Where(t => t is not null)!;

    [Fact]
    public void OldPastedImageStubPointsAtMediaRetrieval()
    {
        var b = new TranscriptBuilder().UserPrompt("look at this");
        b.RawImageMessage("m1", Payload);
        AgeBy(b, AgeIndex.MidAgeTurns + 1);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        string stub = BlockTexts(records).Single(t => t!.Contains("--media"))!;
        Assert.Contains("image/png, 2KB", stub);
        // The pointer addresses the stubbed record's own uuid.
        JsonObject stubbed = records.Single(r =>
            (r["claudinine"] as JsonObject)?["rule"]?.GetValue<string>() == "image-strip");
        Assert.Contains(
            $"claudinine get test-session --ref {stubbed["uuid"]!.GetValue<string>()[..8]} --media",
            stub);
    }

    [Fact]
    public void Base64DocumentBlockIsStubbed()
    {
        var b = new TranscriptBuilder().UserPrompt("here is the spec");
        b.RawDocumentMessage("d1", Payload);
        AgeBy(b, AgeIndex.MidAgeTurns + 1);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        Assert.DoesNotContain(records.SelectMany(r =>
            (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? []),
            x => x["type"]?.GetValue<string>() == "document");
        string stub = BlockTexts(records).Single(t => t!.Contains("--media"))!;
        Assert.Contains("application/pdf", stub);
    }

    [Fact]
    public void SingleCallScreenshotResultImageIsStubbedTextKept()
    {
        // One call: chain-collapse (MinCalls=2) never sees it, so the nested
        // descent in image-strip is the only thing standing between this
        // screenshot and immortality.
        var b = new TranscriptBuilder().UserPrompt("take a screenshot");
        b.ScreenshotToolCall(out _, Payload);
        AgeBy(b, AgeIndex.MidAgeTurns + 1);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        string text = File.ReadAllText(path);
        Assert.DoesNotContain("\"image\"", text);
        Assert.Contains("screenshot taken", text); // sibling text block untouched
        Assert.Contains("--media", text);
    }

    [Fact]
    public void PastedImageRoundTripsThroughGetMedia()
    {
        var b = new TranscriptBuilder().UserPrompt("look");
        b.RawImageMessage("m1", Payload);
        AgeBy(b, AgeIndex.MidAgeTurns + 1);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);
        Compactor.Run(path);

        JsonObject stubbed = Load(path).Single(r =>
            (r["claudinine"] as JsonObject)?["rule"]?.GetValue<string>() == "image-strip");
        string refArg = stubbed["uuid"]!.GetValue<string>()[..8];

        string output = RunGet(["test-session", "--ref", refArg, "--media"], out int rc);
        Assert.Equal(0, rc);
        string decodedPath = Regex.Match(output, @"wrote (.+?) \(image/png").Groups[1].Value;
        Assert.Equal(Payload, File.ReadAllBytes(decodedPath));
        Assert.Contains("Read", output); // tells the model how to view it
    }

    [Fact]
    public void CollapsedScreenshotTurnRefLineNotesMediaAndRoundTrips()
    {
        // Two calls: the turn collapses, the screenshot result record becomes the
        // digest carrier and its nested image survives only in the mirror.
        var b = new TranscriptBuilder().UserPrompt("screenshot then check");
        b.ScreenshotToolCall(out _, Payload);
        b.ToolCall("Bash", new JsonObject { ["command"] = "echo ok" }, "ok");
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        string text = File.ReadAllText(path);
        Assert.Contains("[+media image/png", text);
        Match refMatch = Regex.Match(text, @"\[([0-9a-f-]{8})\] computer\(");
        Assert.True(refMatch.Success, "digest should carry a ref line for the screenshot call");

        string output = RunGet(["test-session", "--ref", refMatch.Groups[1].Value, "--media"], out int rc);
        Assert.Equal(0, rc);
        string decodedPath = Regex.Match(output, @"wrote (.+?) \(image/png").Groups[1].Value;
        Assert.Equal(Payload, File.ReadAllBytes(decodedPath));
    }

    [Fact]
    public void MediaRequiresRef()
    {
        _ = RunGet(["test-session", "--media"], out int rc);
        Assert.Equal(1, rc);
    }

    [Fact]
    public void MediaOnTextOnlyRecordFails()
    {
        var b = new TranscriptBuilder().UserPrompt("plain work");
        b.ToolCall("Bash", new JsonObject { ["command"] = "echo hi" }, "hi there output");
        b.AssistantText("done");
        string path = b.WriteTo(_dir);
        Compactor.Run(path);

        string resultUuid = Load(path).Single(r =>
            (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>()
                .Any(x => x["type"]?.GetValue<string>() == "tool_result") == true)
            ["uuid"]!.GetValue<string>();

        _ = RunGet(["test-session", "--ref", resultUuid[..8], "--media"], out int rc);
        Assert.Equal(1, rc);
    }

    [Fact]
    public void LegacyDeadEndStubIsUpgradedToRetrievalForm()
    {
        const string legacy = "[claudinine: old screenshot removed — re-request if needed]";
        var b = new TranscriptBuilder().UserPrompt("look");
        b.RawImageMessage("m1", Payload);
        AgeBy(b, AgeIndex.MidAgeTurns + 1);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        // Rewrite the image block into the pre-0.1.6 dead-end stub, as an old
        // version would have left it on disk.
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("\"image\"")) continue;
            var rec = (JsonObject)JsonNode.Parse(lines[i])!;
            var blocks = (JsonArray)rec["message"]!["content"]!;
            int bi = blocks.Select((blk, k) => (blk, k))
                .Single(p => ((JsonObject)p.blk!)["type"]!.GetValue<string>() == "image").k;
            blocks[bi] = new JsonObject { ["type"] = "text", ["text"] = legacy };
            lines[i] = rec.ToJsonString();
        }
        File.WriteAllText(path, string.Join("\n", lines) + "\n");

        Compactor.Run(path);

        string text = File.ReadAllText(path);
        Assert.DoesNotContain("re-request if needed", text);
        Assert.Contains("--media", text);
    }

    [Fact]
    public void MediaStubbingIsIdempotent()
    {
        var b = new TranscriptBuilder().UserPrompt("everything at once");
        b.RawImageMessage("m1", Payload);
        b.RawDocumentMessage("d1", Payload);
        b.ScreenshotToolCall(out _, Payload);
        b.ToolCall("Bash", new JsonObject { ["command"] = "echo ok" }, "ok");
        AgeBy(b, AgeIndex.MidAgeTurns + 1);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);
        string afterFirst = File.ReadAllText(path);
        Compactor.Run(path);
        Assert.Equal(afterFirst, File.ReadAllText(path));
    }
}
