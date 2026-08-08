using System.Text.Json.Nodes;
using Claudinine.Rules;
using Xunit;

namespace Claudinine.Tests;

public sealed class AnchorInputStubTests : IDisposable
{
    private readonly string _dir;
    private static readonly string Output = "tool output " + new string('o', 400);
    private static readonly string BigCommand = "python - <<'EOF'\n" + new string('x', 600) + "\nEOF";

    public AnchorInputStubTests()
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
        return b.WriteTo(_dir);
    }

    [Fact]
    public void LargeAnchorInputIsStubbedWithPointerAndPreview()
    {
        string path = BuildSession();

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        JsonObject input = (JsonObject)UseBlock(records)["input"]!;
        string pointer = input["claudinine"]!.GetValue<string>();
        Assert.Contains("claudinine get test-session --ref ", pointer);
        Assert.Contains(" --full", pointer);
        Assert.StartsWith("python - <<'EOF' x", input["preview"]!.GetValue<string>());
        Assert.True(input["preview"]!.GetValue<string>().Length <= 90);
        Assert.Null(input["command"]); // original payload gone

        // The ref in the pointer is the use record's own uuid prefix.
        JsonObject useRec = records.Single(r =>
            (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>()
                .Any(x => x["type"]?.GetValue<string>() == "tool_use") == true);
        Assert.Contains("--ref " + useRec["uuid"]!.GetValue<string>()[..8], pointer);
    }

    [Fact]
    public void OriginalInputIsRetrievableFromMirrorViaGetVerb()
    {
        string path = BuildSession();
        Compactor.Run(path);

        JsonObject[] records = Load(path);
        string pointer = ((JsonObject)UseBlock(records)["input"]!)["claudinine"]!.GetValue<string>();
        string refArg = pointer.Split("--ref ")[1].Split(' ')[0];

        var sw = new StringWriter();
        TextWriter orig = Console.Out;
        Console.SetOut(sw);
        try
        {
            int rc = GetVerb.Run(["test-session", "--ref", refArg, "--full"]);
            Assert.Equal(0, rc);
        }
        finally
        {
            Console.SetOut(orig);
        }
        // The mirror still has the pristine use record: full original command.
        Assert.Contains(new string('x', 600), sw.ToString());
    }

    [Fact]
    public void SmallAnchorInputIsLeftAlone()
    {
        string path = BuildSession(anchorCommand: "echo tiny");

        Compactor.Run(path);

        JsonObject input = (JsonObject)UseBlock(Load(path))["input"]!;
        Assert.Equal("echo tiny", input["command"]?.GetValue<string>());
        Assert.Null(input["claudinine"]);
    }

    [Fact]
    public void NonAnchorInputsAreNeverTouched()
    {
        // A single-call turn does not collapse: its use keeps the full input even
        // though it is large.
        var b = new TranscriptBuilder().UserPrompt("single");
        b.ToolCall("Bash", new JsonObject { ["command"] = BigCommand }, Output);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject input = (JsonObject)UseBlock(Load(path))["input"]!;
        Assert.Equal(BigCommand, input["command"]?.GetValue<string>());
    }

    [Fact]
    public void StubbingIsIdempotent()
    {
        string path = BuildSession();
        Compactor.Run(path);
        string afterFirst = File.ReadAllText(path);
        Compactor.Run(path);
        Assert.Equal(afterFirst, File.ReadAllText(path));
    }

    [Fact]
    public void CarriersAlreadyOnDiskGetTheirAnchorStubbedRetroactively()
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
        Assert.NotNull(input["claudinine"]);
        Assert.Null(input["command"]);
    }
}
