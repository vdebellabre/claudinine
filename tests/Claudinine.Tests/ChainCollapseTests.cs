using System.Text.Json.Nodes;
using Claudinine.Rules;
using Claudinine.Transcript;
using Xunit;

namespace Claudinine.Tests;

public sealed class ChainCollapseTests : IDisposable
{
    private readonly string _dir;
    private static readonly string Output = "tool output " + new string('o', 400);

    public ChainCollapseTests()
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

    /// <summary>An aged 5-call turn followed by fresh turns to age it.</summary>
    private string BuildCollapsibleSession(out List<string> toolUseIds)
    {
        var b = new TranscriptBuilder().UserPrompt("investigate the bug");
        toolUseIds = [];
        for (int i = 0; i < 5; i++)
        {
            b.BashRead($"sed -n '1,5p' file{i}.txt", out string id, Output + i);
            toolUseIds.Add(id);
            b.AssistantText($"note after call {i}");
        }
        b.AssistantText("final answer: it was DNS");
        for (int i = 0; i < AgeIndex.MidAgeTurns + 1; i++)
            b.UserPrompt($"filler {i}").AssistantText("ok");
        return b.WriteTo(_dir);
    }

    [Fact]
    public void AgedMultiCallTurnCollapsesToAnchorPair()
    {
        string path = BuildCollapsibleSession(out var ids);
        int linesBefore = File.ReadAllLines(path).Length;

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        Assert.True(records.Length < linesBefore); // records were actually removed

        // Anchor pair survives with real ids; calls 1..4 are gone as records.
        var remainingUses = records.SelectMany(r =>
            (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>()
                .Where(x => x["type"]?.GetValue<string>() == "tool_use")
                .Select(x => x["id"]!.GetValue<string>()) ?? []).ToList();
        Assert.Contains(ids[0], remainingUses);
        foreach (string id in ids[1..])
            Assert.DoesNotContain(id, remainingUses);

        // The carrier holds the digest: header, one [ref] line per call, notes verbatim.
        string carrier = records.SelectMany(r =>
            (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Single(x => x["tool_use_id"]?.GetValue<string>() == ids[0])["content"]!
            .GetValue<string>();
        Assert.StartsWith("[claudinine: this turn originally ran 5 separate tool calls", carrier);
        Assert.Contains("claudinine get test-session --ref", carrier);
        Assert.Contains("REPORT of past actions", carrier);
        Assert.Equal(5, carrier.Split("] Bash(").Length - 1); // one preview line per call
        Assert.Contains("(note) note after call 2", carrier);
        Assert.Contains("final answer: it was DNS",
            string.Join("\n", records.Select(r => r.ToJsonString()))); // trailing prose kept as record
    }

    [Fact]
    public void ParentChainIsRebuiltOverSurvivors()
    {
        string path = BuildCollapsibleSession(out _);
        Compactor.Run(path);

        JsonObject[] records = Load(path);
        var uuids = records.Select(r => r["uuid"]?.GetValue<string>())
            .Where(u => u is not null).ToHashSet();
        int pos = 0;
        var seen = new HashSet<string>();
        foreach (JsonObject r in records)
        {
            string? parent = r["parentUuid"]?.GetValue<string>();
            if (parent is not null)
                Assert.Contains(parent, seen); // every parent exists EARLIER in the file
            if (r["uuid"]?.GetValue<string>() is string u)
                seen.Add(u);
            pos++;
        }
    }

    [Fact]
    public void CollapseIsIdempotent()
    {
        string path = BuildCollapsibleSession(out _);
        Compactor.Run(path);
        string afterFirst = File.ReadAllText(path);
        Compactor.Run(path);
        Assert.Equal(afterFirst, File.ReadAllText(path));
    }

    [Fact]
    public void RecentTurnIsNotCollapsed()
    {
        var b = new TranscriptBuilder().UserPrompt("fresh work");
        for (int i = 0; i < 5; i++)
            b.BashRead($"sed -n '1,5p' f{i}.txt", out _, Output + i);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);
        string before = File.ReadAllText(path);

        Compactor.Run(path);

        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void SingleCallTurnBelowThresholdIsLeftAlone()
    {
        var b = new TranscriptBuilder().UserPrompt("small turn");
        b.BashRead("sed -n '1,5p' a.txt", out _, "short a");
        for (int i = 0; i < AgeIndex.MidAgeTurns + 1; i++)
            b.UserPrompt($"filler {i}").AssistantText("ok");
        string path = b.WriteTo(_dir);
        int linesBefore = File.ReadAllLines(path).Length;

        Compactor.Run(path);

        Assert.Equal(linesBefore, File.ReadAllLines(path).Length); // nothing removed
    }

    [Fact]
    public void FullOutputsAreRetrievableFromMirrorAfterCollapse()
    {
        string path = BuildCollapsibleSession(out var ids);
        Compactor.Run(path);

        // Every removed output is in the mirror, addressable by the digest's refs.
        string mirror = File.ReadAllText(Claudinine.Mirror.MirrorFile.PathFor(path));
        for (int i = 0; i < 5; i++)
            Assert.Contains(Output + i, mirror);

        JsonObject[] records = Load(path);
        string carrier = records.SelectMany(r =>
            (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Single(x => x["tool_use_id"]?.GetValue<string>() == ids[0])["content"]!
            .GetValue<string>();
        // Refs in the digest resolve to mirror records.
        var refs = System.Text.RegularExpressions.Regex.Matches(carrier, @"^\[([0-9a-f-]{8})\] ",
            System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(5, refs.Count);
        foreach (string r in refs)
            Assert.Contains($"\"uuid\":\"{r}", mirror.Replace("\"uuid\": \"", "\"uuid\":\""));
    }

    [Fact]
    public void LeafUuidAnchorsAreRemappedNotLeftDangling()
    {
        // A last-prompt-style record whose leafUuid points INTO the collapsed span.
        var b = new TranscriptBuilder().UserPrompt("investigate");
        string? removedUuid = null;
        for (int i = 0; i < 5; i++)
        {
            b.BashRead($"sed -n '1,5p' f{i}.txt", out _, Output + i);
            if (i == 3) removedUuid = b.LastUuid; // a result record that will be removed
        }
        b.RawLine($"{{\"type\":\"last-prompt\",\"sessionId\":\"test-session\",\"leafUuid\":\"{removedUuid}\"}}");
        for (int i = 0; i < AgeIndex.MidAgeTurns + 1; i++)
            b.UserPrompt($"filler {i}").AssistantText("ok");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        var uuids = records.Select(r => r["uuid"]?.GetValue<string>()).Where(u => u is not null).ToHashSet();
        JsonObject lastPrompt = records.Single(r => r["type"]?.GetValue<string>() == "last-prompt");
        string? leaf = lastPrompt["leafUuid"]?.GetValue<string>();
        Assert.NotEqual(removedUuid, leaf);      // remapped away from the removed record
        Assert.True(leaf is null || uuids.Contains(leaf)); // and points at a survivor
    }

    [Fact]
    public void TurnWithParallelToolCallsIsSkippedWhole()
    {
        // Hand-build an assistant record with TWO tool_use blocks: fail-closed.
        var b = new TranscriptBuilder().UserPrompt("parallel");
        b.RawLine("""{"type":"assistant","uuid":"00000000-0000-0000-1111-000000000001","parentUuid":null,"sessionId":"test-session","message":{"role":"assistant","content":[{"type":"tool_use","id":"tp1","name":"Bash","input":{"command":"sed -n '1,5p' a.txt"}},{"type":"tool_use","id":"tp2","name":"Bash","input":{"command":"sed -n '1,5p' b.txt"}}]}}""");
        b.RawLine($$$"""{"type":"user","uuid":"00000000-0000-0000-1111-000000000002","parentUuid":"00000000-0000-0000-1111-000000000001","sessionId":"test-session","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tp1","content":"{{{Output}}}"},{"type":"tool_result","tool_use_id":"tp2","content":"{{{Output}}}"}]}}""");
        for (int i = 0; i < 4; i++)
            b.BashRead($"sed -n '1,5p' f{i}.txt", out _, "small");
        for (int i = 0; i < AgeIndex.MidAgeTurns + 1; i++)
            b.UserPrompt($"filler {i}").AssistantText("ok");
        string path = b.WriteTo(_dir);
        int linesBefore = File.ReadAllLines(path).Length;

        Compactor.Run(path);

        Assert.Equal(linesBefore, File.ReadAllLines(path).Length); // no removals anywhere in that turn
    }
}
