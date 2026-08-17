using System.Text.RegularExpressions;

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
    private readonly string _project;

    /// <summary>Recognizable non-trivial bytes for decode round-trip asserts.</summary>
    private static readonly byte[] Payload =
        [.. Enumerable.Range(0, 2048).Select(i => (byte)(i % 251))];

    public MediaRetrievalTests()
    {
        // _dir doubles as the fake HOME: `get` resolves sids by globbing
        // <home>/.claude/projects/*/*/claudinine, so transcripts live in a
        // projects-shaped tree and the verb gets _dir through its home seam.
        _dir = Path.Combine(Path.GetTempPath(), "claudinine-tests", Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_dir, ".claude", "projects", "proj");
        Directory.CreateDirectory(_project);
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

    /// <summary>
    /// GetVerb writes straight to Console; TUnit already redirects it per test, so
    /// we read its capture rather than swapping the writer. Slicing from the
    /// pre-call length keeps repeat calls within one test from re-reporting.
    /// </summary>
    private string RunGet(string[] args, out int rc)
    {
        TestContext ctx = TestContext.Current!;
        int before = ctx.GetStandardOutput().Length;
        rc = GetVerb.Run(args, Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA"), _dir);
        return ctx.GetStandardOutput()[before..];
    }

    /// <summary>All content-block texts of every record, stub texts included.</summary>
    private static IEnumerable<string> BlockTexts(JsonObject[] records) =>
        records.SelectMany(r => (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(b => b["text"]?.GetValue<string>())
            .Where(t => t is not null)!;

    [Test]
    public async Task OldPastedImageStubPointsAtMediaRetrieval()
    {
        var b = new TranscriptBuilder().UserPrompt("look at this");
        b.RawImageMessage("m1", Payload);
        AgeBy(b, AgeIndex.MidAgeTurns + 1);
        b.AssistantText("done");
        string path = b.WriteTo(_project);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        string stub = BlockTexts(records).Single(t => t!.Contains("--media"))!;
        await Assert.That(stub).Contains("image/png, 2KB");
        // The pointer addresses the stubbed record's own uuid.
        JsonObject stubbed = records.Single(r =>
            (r["claudinine"] as JsonObject)?["rule"]?.GetValue<string>() == "image-strip");
        // Launcher form: self-sufficient with no PATH entry (hosted installs).
        await Assert.That(stub).Contains($"/claudinine/run.sh\" get test-session --ref {stubbed["uuid"]!.GetValue<string>()[..8]} --media");
    }

    [Test]
    public async Task Base64DocumentBlockIsStubbed()
    {
        var b = new TranscriptBuilder().UserPrompt("here is the spec");
        b.RawDocumentMessage("d1", Payload);
        AgeBy(b, AgeIndex.MidAgeTurns + 1);
        b.AssistantText("done");
        string path = b.WriteTo(_project);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        await Assert.That(records.SelectMany(r =>
            (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])).DoesNotContain(x => x["type"]?.GetValue<string>() == "document");
        string stub = BlockTexts(records).Single(t => t!.Contains("--media"))!;
        await Assert.That(stub).Contains("application/pdf");
    }

    [Test]
    public async Task SingleCallScreenshotResultImageIsStubbedTextKept()
    {
        // One call: chain-collapse (MinCalls=2) never sees it, so the nested
        // descent in image-strip is the only thing standing between this
        // screenshot and immortality.
        var b = new TranscriptBuilder().UserPrompt("take a screenshot");
        b.ScreenshotToolCall(out _, Payload);
        AgeBy(b, AgeIndex.MidAgeTurns + 1);
        b.AssistantText("done");
        string path = b.WriteTo(_project);

        Compactor.Run(path);

        string text = File.ReadAllText(path);
        await Assert.That(text).DoesNotContain("\"image\"");
        await Assert.That(text).Contains("screenshot taken"); // sibling text block untouched
        await Assert.That(text).Contains("--media");
    }

    [Test]
    public async Task PastedImageRoundTripsThroughGetMedia()
    {
        var b = new TranscriptBuilder().UserPrompt("look");
        b.RawImageMessage("m1", Payload);
        AgeBy(b, AgeIndex.MidAgeTurns + 1);
        b.AssistantText("done");
        string path = b.WriteTo(_project);
        Compactor.Run(path);

        JsonObject stubbed = Load(path).Single(r =>
            (r["claudinine"] as JsonObject)?["rule"]?.GetValue<string>() == "image-strip");
        string refArg = stubbed["uuid"]!.GetValue<string>()[..8];

        string output = RunGet(["test-session", "--ref", refArg, "--media"], out int rc);
        await Assert.That(rc).IsEqualTo(0);
        string decodedPath = Regex.Match(output, @"wrote (.+?) \(image/png").Groups[1].Value;
        await Assert.That(File.ReadAllBytes(decodedPath)).IsEquivalentTo(Payload);
        await Assert.That(output).Contains("Read"); // tells the model how to view it
    }

    [Test]
    public async Task CollapsedScreenshotTurnRefLineNotesMediaAndRoundTrips()
    {
        // Two calls: the turn collapses, the screenshot result record becomes the
        // digest carrier and its nested image survives only in the mirror.
        var b = new TranscriptBuilder().UserPrompt("screenshot then check");
        b.ScreenshotToolCall(out _, Payload);
        b.ToolCall("Bash", new JsonObject { ["command"] = "echo ok" }, "ok");
        b.AssistantText("done");
        string path = b.WriteTo(_project);

        Compactor.Run(path);

        string text = File.ReadAllText(path);
        await Assert.That(text).Contains("[+media image/png");
        Match refMatch = Regex.Match(text, @"\[([0-9a-f-]{8})\] computer\(");
        await Assert.That(refMatch.Success).IsTrue()
            .Because("digest should carry a ref line for the screenshot call");

        string output = RunGet(["test-session", "--ref", refMatch.Groups[1].Value, "--media"], out int rc);
        await Assert.That(rc).IsEqualTo(0);
        string decodedPath = Regex.Match(output, @"wrote (.+?) \(image/png").Groups[1].Value;
        await Assert.That(File.ReadAllBytes(decodedPath)).IsEquivalentTo(Payload);
    }

    [Test]
    public async Task MediaRequiresRef()
    {
        _ = RunGet(["test-session", "--media"], out int rc);
        await Assert.That(rc).IsEqualTo(1);
    }

    [Test]
    public async Task MediaOnTextOnlyRecordFails()
    {
        var b = new TranscriptBuilder().UserPrompt("plain work");
        b.ToolCall("Bash", new JsonObject { ["command"] = "echo hi" }, "hi there output");
        b.AssistantText("done");
        string path = b.WriteTo(_project);
        Compactor.Run(path);

        string resultUuid = Load(path).Single(r =>
            (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>()
                .Any(x => x["type"]?.GetValue<string>() == "tool_result") == true)
            ["uuid"]!.GetValue<string>();

        _ = RunGet(["test-session", "--ref", resultUuid[..8], "--media"], out int rc);
        await Assert.That(rc).IsEqualTo(1);
    }

    [Test]
    public async Task LegacyDeadEndStubIsUpgradedToRetrievalForm()
    {
        const string legacy = "[claudinine: old screenshot removed — re-request if needed]";
        var b = new TranscriptBuilder().UserPrompt("look");
        b.RawImageMessage("m1", Payload);
        AgeBy(b, AgeIndex.MidAgeTurns + 1);
        b.AssistantText("done");
        string path = b.WriteTo(_project);

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
        await Assert.That(text).DoesNotContain("re-request if needed");
        await Assert.That(text).Contains("--media");
    }

    [Test]
    public async Task MediaStubbingIsIdempotent()
    {
        var b = new TranscriptBuilder().UserPrompt("everything at once");
        b.RawImageMessage("m1", Payload);
        b.RawDocumentMessage("d1", Payload);
        b.ScreenshotToolCall(out _, Payload);
        b.ToolCall("Bash", new JsonObject { ["command"] = "echo ok" }, "ok");
        AgeBy(b, AgeIndex.MidAgeTurns + 1);
        b.AssistantText("done");
        string path = b.WriteTo(_project);

        Compactor.Run(path);
        string afterFirst = File.ReadAllText(path);
        Compactor.Run(path);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(afterFirst);
    }
}
