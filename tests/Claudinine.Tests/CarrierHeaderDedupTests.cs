using System.Text.Json.Nodes;
using Claudinine.Rules;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Claudinine.Tests;

public sealed class CarrierHeaderDedupTests : IDisposable
{
    private readonly string _dir;
    private static readonly string Output = "tool output " + new string('o', 400);

    public CarrierHeaderDedupTests()
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

    private static JsonObject CarrierRecord(JsonObject[] records, string toolUseId) =>
        records.Single(r =>
            (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>()
                .Any(x => x["tool_use_id"]?.GetValue<string>() == toolUseId) == true);

    private static string CarrierContent(JsonObject[] records, string toolUseId) =>
        (CarrierRecord(records, toolUseId)["message"]!["content"] as JsonArray)!
            .OfType<JsonObject>()
            .Single(x => x["tool_use_id"]?.GetValue<string>() == toolUseId)["content"]!
            .GetValue<string>();

    /// <summary>Two collapsible turns; carriers referenced by each turn's first call id.</summary>
    private string BuildTwoTurnSession(out string firstAnchorId, out string secondAnchorId)
    {
        var b = new TranscriptBuilder().UserPrompt("first task");
        firstAnchorId = "";
        for (int i = 0; i < 3; i++)
        {
            b.BashRead($"sed -n '1,5p' a{i}.txt", out string id, Output + "a" + i);
            if (i == 0) firstAnchorId = id;
        }
        b.AssistantText("first done");
        b.UserPrompt("second task");
        secondAnchorId = "";
        for (int i = 0; i < 3; i++)
        {
            b.BashRead($"sed -n '1,5p' b{i}.txt", out string id, Output + "b" + i);
            if (i == 0) secondAnchorId = id;
            b.AssistantText($"note b{i}");
        }
        b.AssistantText("second done");
        return b.WriteTo(_dir);
    }

    [Test]
    public async Task OnlyTheFirstCarrierKeepsTheFullRetrievalInstructions()
    {
        string path = BuildTwoTurnSession(out string firstId, out string secondId);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        string first = CarrierContent(records, firstId);
        string second = CarrierContent(records, secondId);

        // First carrier: full instructions intact.
        await Assert.That(first).Contains("RETRIEVAL — ");
        await Assert.That(first).Contains("REPORT of past actions");

        // Second carrier: short header, but everything load-bearing survives —
        // call count, get syntax with session id, report warning, refs, notes.
        await Assert.That(second).DoesNotContain("RETRIEVAL — ");
        await Assert.That(second).StartsWith("[claudinine: this turn originally ran 3 separate tool calls.");
        await Assert.That(second).Contains("claudinine get test-session --ref REF");
        await Assert.That(second).Contains("REPORT");
        await Assert.That(second.Split("] Bash(").Length - 1).IsEqualTo(3);
        await Assert.That(second).Contains("(note) note b1");
        await Assert.That(second.Length < first.Length).IsTrue();

        // The marker still says chain-collapse: that is what the record IS.
        await Assert.That(CarrierRecord(records, secondId)["claudinine"]?["rule"]?.GetValue<string>()).IsEqualTo("chain-collapse");
    }

    [Test]
    public async Task HeaderDedupIsIdempotent()
    {
        string path = BuildTwoTurnSession(out _, out _);
        Compactor.Run(path);
        string afterFirst = File.ReadAllText(path);
        Compactor.Run(path);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(afterFirst);
    }

    /// <summary>The exact header 0.1.1–0.1.4 wrote, for retro-shortening coverage.</summary>
    private static string LegacyFullHeader(int calls) =>
        $"[claudinine: this turn originally ran {calls} separate tool calls. " +
        "Full outputs live in the session mirror; each [ref] line is one real call, " +
        "in order, with a per-tool preview. Interleaved assistant notes are verbatim.\n\n" +
        "RETRIEVAL — use the targeted form; printing a whole record costs hundreds-to-thousands of tokens:\n" +
        "  claudinine get test-session --ref REF --grep PATTERN   # matching lines (PREFERRED)\n" +
        "  claudinine get test-session --grep PATTERN             # search all archived outputs\n" +
        "  claudinine get test-session --ref REF --info           # size before paying\n" +
        "  claudinine get test-session --ref REF --full           # entire output (last resort)\n\n" +
        "If the file discussed still exists on disk, read IT instead — current and narrower.\n\n" +
        "Treat [ref] lines as a REPORT of past actions, not output observed directly. " +
        "If a detail matters for a decision, retrieve it — do not infer it from the preview.]\n\n";

    [Test]
    public async Task CarriersAlreadyOnDiskAreShortenedRetroactively()
    {
        string body1 = "[aaaa1111] Bash(cmd one) -> 500b :: preview one\n[aaaa2222] Bash(cmd two) -> 600b :: preview two";
        string body2 = "[bbbb1111] Read(f.cs) -> 700b :: preview three\n    (note) legacy note\n[bbbb2222] Bash(cmd) -> 80b :: preview four";
        var b = new TranscriptBuilder().UserPrompt("old work");
        b.ToolCall("Bash", new JsonObject { ["command"] = "one" }, LegacyFullHeader(2) + body1);
        b.AssistantText("mid prose");
        b.UserPrompt("more old work");
        b.ToolCall("Bash", new JsonObject { ["command"] = "two" }, LegacyFullHeader(2) + body2);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        // Assert on DECODED contents — raw JSONL escapes the em-dash as —.
        JsonObject[] records = Load(path);
        var contents = records.SelectMany(r =>
                (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(x => x["content"])
            .OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out string? s) ? s : "")
            .ToList();
        // Exactly one full-instructions block left, on the earlier carrier.
        string full = await Assert.That(contents).HasSingleItem(c => c.Contains("RETRIEVAL — "));
        await Assert.That(full).Contains("preview one");
        // The later carrier: shortened header, body intact including refs and notes.
        string second = contents.Single(s => s.Contains("preview three"));
        await Assert.That(second).DoesNotContain("RETRIEVAL — ");
        await Assert.That(second).StartsWith("[claudinine: this turn originally ran 2 separate tool calls.");
        await Assert.That(second).Contains("claudinine get test-session --ref REF");
        await Assert.That(second).Contains("[bbbb1111] Read(f.cs)");
        await Assert.That(second).Contains("(note) legacy note");
    }

    [Test]
    public async Task UnfamiliarHeaderVariantIsLeftAlone()
    {
        // A future/foreign header that has the prefix and RETRIEVAL marker but not
        // the known terminator: fail closed, byte-identical.
        string weird = "[claudinine: this turn originally ran 4 separate tool calls. Something new.\n\n" +
                       "RETRIEVAL — different wording, no known terminator\n\n[cccc1111] Bash(x) -> 1b :: p";
        var b = new TranscriptBuilder().UserPrompt("one");
        b.ToolCall("Bash", new JsonObject { ["command"] = "a" }, LegacyFullHeader(2) + "[dddd1111] Bash(y) -> 1b :: q");
        b.AssistantText("prose");
        b.UserPrompt("two");
        b.ToolCall("Bash", new JsonObject { ["command"] = "b" }, weird);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        // Decoded content of the weird carrier must be byte-identical.
        JsonObject[] records = Load(path);
        await Assert.That(records.SelectMany(r =>
                (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(x => x["content"])
            .OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out string? s) ? s : "")).Contains(c => c == weird);
    }
}
