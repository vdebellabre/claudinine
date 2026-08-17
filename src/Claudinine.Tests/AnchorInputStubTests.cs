namespace Claudinine.Tests;

public sealed class AnchorInputStubTests : IDisposable
{
    private readonly string _dir;
    // Corpus-sized: see ChainCollapseTests.Output — the economics gate makes the
    // fixture payload size load-bearing (412b was below the header break-even).
    private static readonly string Output = "tool output " + new string('o', 2000);
    private static readonly string BigCommand = "python - <<'EOF'\n" + new string('x', 600) + "\nEOF";

    private readonly string _project;

    public AnchorInputStubTests()
    {
        // _dir doubles as the fake HOME so `get` can resolve the session through
        // its home seam (colocated mirrors are found by globbing
        // <home>/.claude/projects/*/*/claudinine).
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

    /// <summary>The single surviving tool_use block (post-collapse, only the anchor remains).</summary>
    private static JsonObject UseBlock(JsonObject[] records) =>
        records.SelectMany(r =>
            (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Single(x => x["type"]?.GetValue<string>() == "tool_use");

    /// <summary>A collapsible turn whose FIRST call (the future anchor) has a big input.</summary>
    private string BuildSession(string? anchorCommand = null)
    {
        var b = new TranscriptBuilder().UserPrompt("do the thing");
        b.ToolCall("Bash", new JsonObject { ["command"] = anchorCommand ?? BigCommand }, Output + "0");
        for (int i = 1; i < 3; i++)
            b.ToolCall("Bash", new JsonObject { ["command"] = $"echo step {i}" }, Output + i);
        b.AssistantText("done");
        return b.WriteTo(_project);
    }

    [Test]
    public async Task LargeAnchorInputIsStubbedWithPointerAndPreview()
    {
        string path = BuildSession();

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        JsonObject input = (JsonObject)UseBlock(records)["input"]!;
        string pointer = input["claudinine"]!.GetValue<string>();
        // A pointer, not a command: the carrier's own RETRIEVAL block (same
        // boundary segment, kept full by header dedup) teaches the how.
        await Assert.That(pointer).StartsWith("input archived at collapse; original: ref ");
        await Assert.That(pointer).Contains("RETRIEVAL block");
        await Assert.That(pointer).DoesNotContain("claudinine get");
        await Assert.That(input["preview"]!.GetValue<string>()).StartsWith("python - <<'EOF' x");
        await Assert.That(input["preview"]!.GetValue<string>().Length <= 90).IsTrue();
        await Assert.That(input["command"]).IsNull(); // original payload gone

        // The ref in the pointer is the use record's own uuid prefix.
        JsonObject useRec = records.Single(r =>
            (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>()
                .Any(x => x["type"]?.GetValue<string>() == "tool_use") == true);
        await Assert.That(pointer).Contains("ref " + useRec["uuid"]!.GetValue<string>()[..8]);
    }

    [Test]
    public async Task OriginalInputIsRetrievableFromMirrorViaGetVerb()
    {
        string path = BuildSession();
        Compactor.Run(path);

        JsonObject[] records = Load(path);
        string pointer = ((JsonObject)UseBlock(records)["input"]!)["claudinine"]!.GetValue<string>();
        string refArg = pointer.Split("original: ref ")[1].Split(' ')[0];

        // GetVerb writes to Console, which TUnit captures per test.
        int rc = GetVerb.Run(["test-session", "--ref", refArg, "--full"],
            Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA"), _dir);
        await Assert.That(rc).IsEqualTo(0);

        // The mirror still has the pristine use record: full original command.
        await Assert.That(TestContext.Current!.GetStandardOutput()).Contains(new string('x', 600));
    }

    [Test]
    public async Task SmallAnchorInputIsLeftAlone()
    {
        string path = BuildSession(anchorCommand: "echo tiny");

        Compactor.Run(path);

        JsonObject input = (JsonObject)UseBlock(Load(path))["input"]!;
        await Assert.That(input["command"]?.GetValue<string>()).IsEqualTo("echo tiny");
        await Assert.That(input["claudinine"]).IsNull();
    }

    [Test]
    public async Task NonAnchorInputsAreNeverTouched()
    {
        // Anchor-input stubbing targets the ANCHOR only. A turn that does not collapse
        // at all must keep every input verbatim — here the payload is too small to pay
        // for a digest, so chain-collapse declines and the large input survives.
        var b = new TranscriptBuilder().UserPrompt("single");
        b.ToolCall("Bash", new JsonObject { ["command"] = BigCommand }, "tiny");
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject input = (JsonObject)UseBlock(Load(path))["input"]!;
        await Assert.That(input["command"]?.GetValue<string>()).IsEqualTo(BigCommand);
    }

    [Test]
    public async Task StubbingIsIdempotent()
    {
        string path = BuildSession();
        Compactor.Run(path);
        string afterFirst = File.ReadAllText(path);
        Compactor.Run(path);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(afterFirst);
    }

    [Test]
    public async Task CarriersAlreadyOnDiskGetTheirAnchorStubbedRetroactively()
    {
        // Simulates a file collapsed by an earlier version: the carrier content is
        // already a digest, the anchor use still has its full input on disk.
        var b = new TranscriptBuilder().UserPrompt("old work");
        b.ToolCall("Bash", new JsonObject { ["command"] = BigCommand },
            "[claudinine: this turn originally ran 4 separate tool calls. …]\n\n[aaaa1111] Bash(x) -> 1b :: p");
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject input = (JsonObject)UseBlock(Load(path))["input"]!;
        await Assert.That(input["claudinine"]).IsNotNull();
        await Assert.That(input["command"]).IsNull();
    }
}
