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

    [Test]
    public async Task AgedMultiCallTurnCollapsesToAnchorPair()
    {
        string path = BuildCollapsibleSession(out var ids);
        int linesBefore = File.ReadAllLines(path).Length;

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        await Assert.That(records.Length < linesBefore).IsTrue(); // records were actually removed

        // Anchor pair survives with real ids; calls 1..4 are gone as records.
        var remainingUses = records.SelectMany(r =>
            (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>()
                .Where(x => x["type"]?.GetValue<string>() == "tool_use")
                .Select(x => x["id"]!.GetValue<string>()) ?? []).ToList();
        await Assert.That(remainingUses).Contains(ids[0]);
        foreach (string id in ids[1..])
            await Assert.That(remainingUses).DoesNotContain(id);

        // The carrier holds the digest: header, one [ref] line per call, notes verbatim.
        string carrier = records.SelectMany(r =>
            (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Single(x => x["tool_use_id"]?.GetValue<string>() == ids[0])["content"]!
            .GetValue<string>();
        await Assert.That(carrier).StartsWith("[claudinine: this turn originally ran 5 separate tool calls");
        await Assert.That(carrier).Contains("claudinine get test-session --ref");
        await Assert.That(carrier).Contains("REPORT of past actions");
        await Assert.That(carrier.Split("] Bash(").Length - 1).IsEqualTo(5); // one preview line per call
        await Assert.That(carrier).Contains("(note) note after call 2");
        await Assert.That(string.Join("\n", records.Select(r => r.ToJsonString())))
            .Contains("final answer: it was DNS"); // trailing prose kept as record
    }

    [Test]
    public async Task ParentChainIsRebuiltOverSurvivors()
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
                await Assert.That(seen).Contains(parent); // every parent exists EARLIER in the file
            if (r["uuid"]?.GetValue<string>() is string u)
                seen.Add(u);
            pos++;
        }
    }

    [Test]
    public async Task CollapseIsIdempotent()
    {
        string path = BuildCollapsibleSession(out _);
        Compactor.Run(path);
        string afterFirst = File.ReadAllText(path);
        Compactor.Run(path);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(afterFirst);
    }

    [Test]
    public async Task FreshSettledTurnCollapsesToo()
    {
        // No age gate by design: the app never re-reads the file mid-session, so
        // even the newest settled turn is fair game — the payout is at next load.
        var b = new TranscriptBuilder().UserPrompt("fresh work");
        for (int i = 0; i < 5; i++)
            b.BashRead($"sed -n '1,5p' f{i}.txt", out _, Output + i);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);
        int linesBefore = File.ReadAllLines(path).Length;

        Compactor.Run(path);

        await Assert.That(File.ReadAllLines(path).Length < linesBefore).IsTrue();
        await Assert.That(File.ReadAllText(path)).Contains("this turn originally ran 5 separate tool calls");
    }

    [Test]
    public async Task SingleCallTurnBelowThresholdIsLeftAlone()
    {
        var b = new TranscriptBuilder().UserPrompt("small turn");
        b.BashRead("sed -n '1,5p' a.txt", out _, "short a");
        for (int i = 0; i < AgeIndex.MidAgeTurns + 1; i++)
            b.UserPrompt($"filler {i}").AssistantText("ok");
        string path = b.WriteTo(_dir);
        int linesBefore = File.ReadAllLines(path).Length;

        Compactor.Run(path);

        await Assert.That(File.ReadAllLines(path).Length).IsEqualTo(linesBefore); // nothing removed
    }

    [Test]
    public async Task FullOutputsAreRetrievableFromMirrorAfterCollapse()
    {
        string path = BuildCollapsibleSession(out var ids);
        Compactor.Run(path);

        // Every removed output is in the mirror, addressable by the digest's refs.
        string mirror = File.ReadAllText(Claudinine.Mirror.MirrorLocator.PathFor(path));
        for (int i = 0; i < 5; i++)
            await Assert.That(mirror).Contains(Output + i);

        JsonObject[] records = Load(path);
        string carrier = records.SelectMany(r =>
            (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Single(x => x["tool_use_id"]?.GetValue<string>() == ids[0])["content"]!
            .GetValue<string>();
        // Refs in the digest resolve to mirror records.
        var refs = System.Text.RegularExpressions.Regex.Matches(carrier, @"^\[([0-9a-f-]{8})\] ",
            System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();
        await Assert.That(refs.Count).IsEqualTo(5);
        foreach (string r in refs)
            await Assert.That(mirror.Replace("\"uuid\": \"", "\"uuid\":\"")).Contains($"\"uuid\":\"{r}");
    }

    [Test]
    public async Task LeafUuidAnchorsAreRemappedNotLeftDangling()
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
        await Assert.That(leaf).IsNotEqualTo(removedUuid);      // remapped away from the removed record
        await Assert.That(leaf is null || uuids.Contains(leaf)).IsTrue(); // and points at a survivor
    }

    /// <summary>Every surviving parentUuid must resolve to a record EARLIER in the file.</summary>
    private static async Task AssertParentsResolveEarlier(JsonObject[] records)
    {
        var seen = new HashSet<string>();
        foreach (JsonObject r in records)
        {
            if (r["parentUuid"]?.GetValue<string>() is string parent)
                await Assert.That(seen).Contains(parent);
            if (r["uuid"]?.GetValue<string>() is string u)
                seen.Add(u);
        }
    }

    private static List<string> RemainingUseIds(JsonObject[] records) =>
        records.SelectMany(r =>
            (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>()
                .Where(x => x["type"]?.GetValue<string>() == "tool_use")
                .Select(x => x["id"]!.GetValue<string>()) ?? []).ToList();

    private static string CarrierContent(JsonObject[] records, string toolUseId) =>
        records.SelectMany(r =>
            (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Single(x => x["tool_use_id"]?.GetValue<string>() == toolUseId)["content"]!
            .GetValue<string>();

    [Test]
    public async Task ParallelBatchCollapsesWithInOrderResults()
    {
        // Modern batch: consecutive single-use assistant records, then the results.
        var b = new TranscriptBuilder().UserPrompt("run these in parallel");
        b.ToolUse("cmd-a", out string idA, out string uuidA);
        b.ToolUse("cmd-b", out string idB, out string uuidB);
        b.ToolResultFor(idA, uuidA, Output + "A");
        b.ToolResultFor(idB, uuidB, Output + "B");
        b.BashRead("sed -n '1,5p' seq.txt", out string idC, Output + "C");
        b.AssistantText("done");
        string path = b.WriteTo(_dir);
        int linesBefore = File.ReadAllLines(path).Length;

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        await Assert.That(records.Length < linesBefore).IsTrue();

        // Anchor = the batch's FIRST use; batch sibling and the sequential call collapse.
        var remainingUses = RemainingUseIds(records);
        await Assert.That(remainingUses).Contains(idA);
        await Assert.That(remainingUses).DoesNotContain(idB);
        await Assert.That(remainingUses).DoesNotContain(idC);

        string carrier = CarrierContent(records, idA);
        await Assert.That(carrier).StartsWith("[claudinine: this turn originally ran 3 separate tool calls");
        await Assert.That(System.Text.RegularExpressions.Regex.Matches(carrier,
            @"^\[[0-9a-f-]{8}\] ", System.Text.RegularExpressions.RegexOptions.Multiline).Count).IsEqualTo(3);

        await AssertParentsResolveEarlier(records);
    }

    [Test]
    public async Task ParallelBatchCollapsesWithOutOfOrderResults()
    {
        // Results arrive in COMPLETION order, not call order (real specimen: USE:Bash,
        // USE:Glob, RES:Glob, RES:Bash). The anchor must still be the FIRST use's
        // pair, or that use would survive without its result.
        var b = new TranscriptBuilder().UserPrompt("out of order");
        b.ToolUse("cmd-a", out string idA, out string uuidA);
        b.ToolUse("cmd-b", out string idB, out string uuidB);
        b.ToolResultFor(idB, uuidB, Output + "B"); // b's result lands first
        b.ToolResultFor(idA, uuidA, Output + "A");
        b.AssistantText("done");
        string path = b.WriteTo(_dir);
        int linesBefore = File.ReadAllLines(path).Length;

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        await Assert.That(records.Length < linesBefore).IsTrue();

        var remainingUses = RemainingUseIds(records);
        await Assert.That(remainingUses).Contains(idA);
        await Assert.That(remainingUses).DoesNotContain(idB);

        string carrier = CarrierContent(records, idA);
        await Assert.That(carrier).StartsWith("[claudinine: this turn originally ran 2 separate tool calls");

        await AssertParentsResolveEarlier(records);
    }

    [Test]
    public async Task UuidlessRecordsInsideBatchSpanSurviveWithLeafRemapped()
    {
        // Real specimen: last-prompt/custom-title records interleave INSIDE a batch,
        // between the uses and their results, and last-prompt's leafUuid can point
        // at a batch record the collapse removes.
        var b = new TranscriptBuilder().UserPrompt("interleaved");
        b.ToolUse("cmd-a", out string idA, out string uuidA);
        b.ToolUse("cmd-b", out string idB, out string uuidB);
        b.RawLine($$"""{"type":"last-prompt","sessionId":"test-session","leafUuid":"{{uuidB}}"}""");
        b.RawLine("""{"type":"custom-title","customTitle":"t","sessionId":"test-session"}""");
        b.ToolResultFor(idB, uuidB, Output + "B");
        b.ToolResultFor(idA, uuidA, Output + "A");
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        await Assert.That(RemainingUseIds(records)).DoesNotContain(idB); // collapse did happen

        await Assert.That(records).HasSingleItem(r => r["type"]?.GetValue<string>() == "custom-title");
        JsonObject lastPrompt = records.Single(r => r["type"]?.GetValue<string>() == "last-prompt");
        string? leaf = lastPrompt["leafUuid"]?.GetValue<string>();
        var uuids = records.Select(r => r["uuid"]?.GetValue<string>()).Where(u => u is not null).ToHashSet();
        await Assert.That(leaf).IsNotEqualTo(uuidB);
        await Assert.That(leaf is null || uuids.Contains(leaf)).IsTrue();
    }

    [Test]
    public async Task InterruptedTurnEndingAtResultIsSkippedButEarlierTurnsStillCollapse()
    {
        // The file's final record must never be removed: TryRewrite would abort the
        // WHOLE rewrite, silently discarding every other rule's work. The rule must
        // skip such a turn itself.
        var b = new TranscriptBuilder().UserPrompt("first");
        for (int i = 0; i < 3; i++)
            b.BashRead($"sed -n '1,5p' t1f{i}.txt", out _, Output + i);
        b.AssistantText("turn one done");
        b.UserPrompt("second"); // interrupted turn: file ends exactly at a tool_result
        b.BashRead("sed -n '1,5p' t2a.txt", out string keptA, Output + "x");
        b.BashRead("sed -n '1,5p' t2b.txt", out string keptB, Output + "y");
        string path = b.WriteTo(_dir);
        string lastLineBefore = File.ReadAllLines(path)[^1];

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        // Turn 1 collapsed — the rewrite as a whole landed…
        await Assert.That(string.Join("\n", records.Select(r => r.ToJsonString())))
            .Contains("this turn originally ran 3 separate tool calls");
        // …while the interrupted turn is intact, tail byte-identical.
        var remainingUses = RemainingUseIds(records);
        await Assert.That(remainingUses).Contains(keptA);
        await Assert.That(remainingUses).Contains(keptB);
        await Assert.That(File.ReadAllLines(path)[^1]).IsEqualTo(lastLineBefore);
    }

    [Test]
    public async Task InterleavedBatchCollapses()
    {
        // USE a, USE b, RES a, USE c, RES b, RES c — overlapping batches, the shape a
        // slow call produces when later ones are issued before it answers. Pairing is
        // by tool_use_id, so this collapses like any other batch: anchor = first use,
        // span = first use → last result, every other pair removed whole.
        var b = new TranscriptBuilder().UserPrompt("interleaved batches");
        b.ToolUse("cmd-a", out string idA, out string uuidA);
        b.ToolUse("cmd-b", out string idB, out string uuidB);
        b.ToolResultFor(idA, uuidA, Output + "A");
        b.ToolUse("cmd-c", out string idC, out string uuidC);
        b.ToolResultFor(idB, uuidB, Output + "B");
        b.ToolResultFor(idC, uuidC, Output + "C");
        b.AssistantText("done");
        string path = b.WriteTo(_dir);
        int linesBefore = File.ReadAllLines(path).Length;

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        await Assert.That(records.Length < linesBefore).IsTrue();

        // Anchor is the FIRST use in file order; the other two pairs are gone whole.
        var remainingUses = RemainingUseIds(records);
        await Assert.That(remainingUses).Contains(idA);
        await Assert.That(remainingUses).DoesNotContain(idB);
        await Assert.That(remainingUses).DoesNotContain(idC);

        // Pairs are atomic: no result may outlive its removed use, or its
        // tool_use_id dangles.
        var remainingResultIds = records.SelectMany(r =>
            (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>()
                .Where(x => x["type"]?.GetValue<string>() == "tool_result")
                .Select(x => x["tool_use_id"]!.GetValue<string>()) ?? []).ToList();
        await Assert.That(remainingResultIds).DoesNotContain(idB);
        await Assert.That(remainingResultIds).DoesNotContain(idC);

        // All three calls are accounted for in the digest.
        string carrier = CarrierContent(records, idA);
        await Assert.That(carrier).StartsWith("[claudinine: this turn originally ran 3 separate tool calls");

        await AssertParentsResolveEarlier(records);
    }

    [Test]
    public async Task SourceToolAssistantUuidNeverDanglesAfterCollapse()
    {
        var b = new TranscriptBuilder().UserPrompt("batch");
        b.ToolUse("cmd-a", out string idA, out string uuidA);
        b.ToolUse("cmd-b", out string idB, out string uuidB);
        b.ToolResultFor(idB, uuidB, Output + "B");
        b.ToolResultFor(idA, uuidA, Output + "A");
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        await Assert.That(RemainingUseIds(records)).DoesNotContain(idB); // collapse did happen
        var uuids = records.Select(r => r["uuid"]?.GetValue<string>()).Where(u => u is not null).ToHashSet();
        foreach (JsonObject r in records)
        {
            if (r["sourceToolAssistantUUID"]?.GetValue<string>() is string src)
                await Assert.That(uuids).Contains(src);
        }
    }

    [Test]
    public async Task TurnWithLegacyMultiUseRecordIsSkippedWhole()
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

        await Assert.That(File.ReadAllLines(path).Length).IsEqualTo(linesBefore); // no removals anywhere in that turn
    }
}
