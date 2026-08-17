namespace Claudinine.Tests;

public sealed class CarrierHeaderDedupTests : IDisposable
{
    private readonly string _dir;
    // Corpus-sized: see ChainCollapseTests.Output — the economics gate makes the
    // fixture payload size load-bearing (412b was below the header break-even).
    private static readonly string Output = "tool output " + new string('o', 2000);

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
        // Never describe assistant content as spliced into the tool result —
        // Fable 5's safeguards block every resume over that sentence (see
        // ChainCollapseRule.Header's constraint comment).
        await Assert.That(first).DoesNotContain("Interleaved assistant notes");

        // Second carrier: short header, but everything load-bearing survives —
        // call count, the pointer to the kept RETRIEVAL block, the REF binding,
        // report warning, refs, notes. No command and no sid on purpose: a bare
        // `claudinine` resolves nowhere on hosted installs and a baked path goes
        // stale when trees move (docs/cowork-compatibility.md E8).
        await Assert.That(second).DoesNotContain("RETRIEVAL — ");
        await Assert.That(second).StartsWith("[claudinine: this turn originally ran 3 separate tool calls.");
        await Assert.That(second).Contains("RETRIEVAL block of the nearest earlier collapsed turn");
        await Assert.That(second).Contains("REF = the 8-hex id in [brackets]");
        await Assert.That(second).DoesNotContain("claudinine get");
        await Assert.That(second).Contains("REPORT");
        await Assert.That(second.Split("] Bash(").Length - 1).IsEqualTo(3);
        await Assert.That(second).Contains("(note) note b1");
        await Assert.That(second.Length < first.Length).IsTrue();

        // The marker still says chain-collapse: that is what the record IS.
        await Assert.That(CarrierRecord(records, secondId)["claudinine"]?["rule"]?.GetValue<string>()).IsEqualTo("chain-collapse");
    }

    [Test]
    public async Task EachBoundarySegmentKeepsItsOwnFullHeader()
    {
        // The app's next load slices from the LAST compact_boundary, so a short
        // header's pointer at a pre-boundary block would aim at instructions the
        // model can no longer see. Each segment's first carrier stays full.
        var b = new TranscriptBuilder().UserPrompt("first task");
        b.BashRead("sed -n '1,5p' a0.txt", out string firstId, Output + "a0");
        b.BashRead("sed -n '1,5p' a1.txt", out _, Output + "a1");
        b.AssistantText("first done");
        // The boundary lands in a call-free turn of its own: a protected record
        // inside a collapsible span correctly aborts that span, which is not
        // what this test is about.
        b.UserPrompt("checkpoint");
        b.AssistantText("compacting");
        b.RawLine(new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "compact_boundary",
            ["uuid"] = "99999999-0000-0000-0000-000000000042",
            ["parentUuid"] = null,
            ["sessionId"] = "test-session",
        }.ToJsonString());
        b.UserPrompt("second task");
        b.BashRead("sed -n '1,5p' b0.txt", out string secondId, Output + "b0");
        b.BashRead("sed -n '1,5p' b1.txt", out _, Output + "b1");
        b.AssistantText("second done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        await Assert.That(CarrierContent(records, firstId)).Contains("RETRIEVAL — ");
        await Assert.That(CarrierContent(records, secondId)).Contains("RETRIEVAL — ");
    }

    /// <summary>The exact short header 0.3.x–0.4.x wrote: a bare `claudinine get`
    /// that resolves nowhere on hosted installs (docs/cowork E8).</summary>
    private static string OldShortHeader(int calls) =>
        $"[claudinine: this turn originally ran {calls} separate tool calls. " +
        "Full outputs: claudinine get test-session --ref REF [--grep PATTERN | --info | --full | --media] " +
        "(full retrieval guidance in the first collapsed block of this session; if the file " +
        "discussed still exists on disk, read IT instead). " +
        "[ref] lines are a REPORT, not observed output — retrieve, don't infer.]\n\n";

    [Test]
    public async Task OldShortHeadersAreUpgradedToThePointerForm()
    {
        string body = "[cccc1111] Bash(cmd) -> 500b :: preview five";
        var b = new TranscriptBuilder().UserPrompt("old work");
        b.ToolCall("Bash", new JsonObject { ["command"] = "one" }, OldShortHeader(2) + body);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);
        string afterFirst = File.ReadAllText(path);

        JsonObject[] records = Load(path);
        var carrier = records.SelectMany(r =>
                (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(x => x["content"])
            .OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out string? s) ? s : "")
            .Single(s => s.Contains("preview five"));
        await Assert.That(carrier).DoesNotContain("claudinine get");
        await Assert.That(carrier).Contains("RETRIEVAL block of the nearest earlier collapsed turn");
        await Assert.That(carrier).Contains("[cccc1111] Bash(cmd)"); // body intact

        // And the upgrade is a fixpoint.
        Compactor.Run(path);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(afterFirst);
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

    /// <summary>The exact FABLE-BLOCKING sentence old headers carried, split so
    /// this source file never contains it contiguously (same reason as
    /// CarrierHeaderDedupRule.LegacySentence).</summary>
    private const string LegacySentence =
        " Interleaved assistant" + " notes are verbatim.";

    /// <summary>The exact header 0.1.1–0.1.4 wrote, for retro-shortening coverage.</summary>
    private static string LegacyFullHeader(int calls) =>
        $"[claudinine: this turn originally ran {calls} separate tool calls. " +
        "Full outputs live in the session mirror; each [ref] line is one real call, " +
        $"in order, with a per-tool preview.{LegacySentence}\n\n" +
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
        // Exactly one full-instructions block left, on the earlier carrier —
        // with the Fable-blocking sentence healed out of the kept legacy header.
        string full = await Assert.That(contents).HasSingleItem(c => c.Contains("RETRIEVAL — "));
        await Assert.That(full).Contains("preview one");
        await Assert.That(full).DoesNotContain("Interleaved assistant notes");
        // The later carrier: shortened header, body intact including refs and notes.
        string second = contents.Single(s => s.Contains("preview three"));
        await Assert.That(second).DoesNotContain("RETRIEVAL — ");
        await Assert.That(second).StartsWith("[claudinine: this turn originally ran 2 separate tool calls.");
        await Assert.That(second).Contains("RETRIEVAL block of the nearest earlier collapsed turn");
        await Assert.That(second).Contains("[bbbb1111] Read(f.cs)");
        await Assert.That(second).Contains("(note) legacy note");
        await Assert.That(second).DoesNotContain("Interleaved assistant notes");
    }

    // The heal must reach a [ref] preview quoting the sentence (a session
    // working on this very codebase), not just the header — the safeguard
    // trips on the sentence wherever it sits in the tool result.
    [Test]
    public async Task LegacySentenceQuotedInAPreviewIsHealedToo()
    {
        string body = "[eeee1111] Read(ChainCollapseRule.cs) -> 900b :: " +
            $"preview quoting:{LegacySentence} end";
        var b = new TranscriptBuilder().UserPrompt("old work");
        b.ToolCall("Bash", new JsonObject { ["command"] = "one" }, LegacyFullHeader(2) + body);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        string content = Load(path).SelectMany(r =>
                (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(x => x["content"])
            .OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out string? s) ? s : "")
            .Single(c => c.Contains("[eeee1111]"));
        await Assert.That(content).DoesNotContain("Interleaved assistant notes");
        await Assert.That(content).Contains("preview quoting: end");
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

    [Test]
    public async Task StaleLauncherPathIsRehealedAfterAMove()
    {
        // An absolute launcher path goes stale exactly when the tree moves
        // (cloud↔local, home rename) — and the colocated mirror moved WITH the
        // transcript, so retrieval must keep working at the new location.
        string path = BuildTwoTurnSession(out string firstId, out _);
        Compactor.Run(path);
        string staleLauncher = Launcher.HeaderPathFor(path);

        string movedDir = Path.Combine(_dir, "moved");
        Directory.CreateDirectory(movedDir);
        string movedPath = Path.Combine(movedDir, "test-session.jsonl");
        File.Move(path, movedPath);
        Directory.Move(
            Path.Combine(_dir, "test-session"),
            Path.Combine(movedDir, "test-session"));

        Compactor.Run(movedPath);

        string first = CarrierContent(Load(movedPath), firstId);
        await Assert.That(first).Contains($"sh \"{Launcher.HeaderPathFor(movedPath)}\" get test-session");
        await Assert.That(first).DoesNotContain(staleLauncher);

        // And the heal is a fixpoint: the next pass changes nothing.
        string afterHeal = File.ReadAllText(movedPath);
        Compactor.Run(movedPath);
        await Assert.That(File.ReadAllText(movedPath)).IsEqualTo(afterHeal);
    }

    [Test]
    public async Task LegacyCommandBlockIsUpgradedToTheLauncherForm()
    {
        // Pre-launcher (0.1.x/0.2.x) headers spell bare `claudinine get` — dead
        // on a hosted install where nothing is on PATH. The kept full header's
        // command block is regenerated in the launcher form; everything outside
        // the block stays byte-identical.
        string body = "[aaaa1111] Bash(cmd one) -> 500b :: preview one";
        var b = new TranscriptBuilder().UserPrompt("old work");
        b.ToolCall("Bash", new JsonObject { ["command"] = "one" }, LegacyFullHeader(2) + body);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        string content = records.SelectMany(r =>
                (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(x => x["content"])
            .OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out string? s) ? s : "")
            .Single(c => c.Contains("RETRIEVAL — "));
        await Assert.That(content).Contains($"sh \"{Launcher.HeaderPathFor(path)}\" get test-session --ref REF --grep");
        await Assert.That(content).DoesNotContain("  claudinine get test-session");
        // Outside the command block: untouched.
        await Assert.That(content).StartsWith("[claudinine: this turn originally ran 2 separate tool calls.");
        await Assert.That(content).Contains("If the file discussed still exists on disk");
        await Assert.That(content).Contains("preview one");
    }

    [Test]
    public async Task HealNeverTouchesATailCarrier()
    {
        // The file's final record is never replaced (TryRewrite would refuse the
        // whole pass); a session ending exactly at the full carrier heals later.
        var b = new TranscriptBuilder().UserPrompt("old work");
        b.ToolCall("Bash", new JsonObject { ["command"] = "one" },
            LegacyFullHeader(2) + "[aaaa1111] Bash(cmd one) -> 500b :: preview one");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        await Assert.That(File.ReadAllText(path)).Contains("  claudinine get test-session --ref REF");
    }

    [Test]
    public async Task SlimmingASingleCallCarrierKeepsTheSingularPhrase()
    {
        // The economics gate admits single-call turns, so "1 tool call" carriers exist.
        // Slimming rebuilds the phrase from a parsed count, and spelling it separately
        // here would rewrite them to the ungrammatical "1 separate tool calls" — the two
        // headers must share ChainCollapseRule.CallCountPhrase.
        //
        // Two turns each collapsing ONE fat call: the first keeps full instructions, the
        // second is slimmed, so the slimmed path sees a singular count.
        var b = new TranscriptBuilder().UserPrompt("one");
        b.ToolCall("Bash", new JsonObject { ["command"] = "a" }, Output + "A");
        b.AssistantText("prose");
        b.UserPrompt("two");
        b.ToolCall("Bash", new JsonObject { ["command"] = "b" }, Output + "B");
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        string text = File.ReadAllText(path);
        await Assert.That(text).Contains("originally ran 1 tool call.");
        await Assert.That(text).DoesNotContain("1 separate tool calls");
    }
}
