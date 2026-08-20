namespace Claudinine.Tests;

public sealed class NoiseRulesTests : IDisposable
{
    private readonly string _dir;

    public NoiseRulesTests()
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

    private static string AllText(string path) => File.ReadAllText(path);

    /// <summary>Append N empty user turns so earlier records age past the given turn count.</summary>
    private static TranscriptBuilder AgeBy(TranscriptBuilder b, int turns)
    {
        for (int i = 0; i < turns; i++)
            b.UserPrompt($"turn filler {i}").AssistantText("ok");
        return b;
    }

    // ---- system-reminder-dedup ----

    [Test]
    public async Task DuplicateSystemRemindersRemovedFirstKept()
    {
        string reminder = "<system-reminder>always use tabs " + new string('r', 300) + "</system-reminder>";
        var b = new TranscriptBuilder()
            .UserPrompt("first ask\n" + reminder)
            .AssistantText("noted")
            .UserPrompt("second ask\n" + reminder)
            .AssistantText("still noted")
            .UserPrompt("third ask")
            .AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        string text = AllText(path);
        int occurrences = text.Split("always use tabs").Length - 1;
        await Assert.That(occurrences).IsEqualTo(1); // only the first copy survives
    }

    [Test]
    public async Task DuplicateReminderWithinOneMessageKeepsFirst()
    {
        // Regression: removal by value (Replace) also erased the first copy when
        // the same reminder repeated inside a single text — it then survived
        // nowhere. Removal must be positional.
        string reminder = "<system-reminder>always use tabs " + new string('r', 300) + "</system-reminder>";
        var b = new TranscriptBuilder()
            .UserPrompt("ask\n" + reminder + "\nmore context\n" + reminder)
            .AssistantText("noted")
            .UserPrompt("done")
            .AssistantText("ok");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        string text = AllText(path);
        await Assert.That(text.Split("always use tabs").Length - 1).IsEqualTo(1);
    }

    [Test]
    public async Task DupReminderInTailRecordDoesNotRefuseThePass()
    {
        // B1 (docs/source-analysis.md): a session killed mid-turn can end on a
        // reminder-bearing user record. Replacing the tail makes TryRewrite
        // refuse the WHOLE pass — the rule must skip the tail and converge later.
        string reminder = "<system-reminder>always use tabs " + new string('r', 300) + "</system-reminder>";
        var b = new TranscriptBuilder()
            .UserPrompt("first ask\n" + reminder)
            .AssistantText("noted")
            .UserPrompt("second ask\n" + reminder)
            .AssistantText("still noted")
            .UserPrompt("killed mid-turn\n" + reminder); // file ends on the dup
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        string text = AllText(path);
        // The mid-file dup went (the pass was NOT refused); the tail copy stays.
        await Assert.That(text.Split("always use tabs").Length - 1).IsEqualTo(2);
    }

    // ---- document-dedup ----

    [Test]
    public async Task LargeDuplicateBlocksStubbedAfterFirst()
    {
        string doc = "PROJECT RULES\n" + new string('d', 1500);
        var b = new TranscriptBuilder()
            .UserPrompt("look")
            .AssistantText(doc)
            .UserPrompt("again")
            .AssistantText(doc)
            .UserPrompt("done")
            .AssistantText("ok");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        string text = AllText(path);
        await Assert.That(text.Split(new string('d', 1500)).Length - 1).IsEqualTo(1);
        await Assert.That(text).Contains("duplicate content removed");
        await Assert.That(text).Contains("first seen earlier: PROJECT RULES");
    }

    [Test]
    public async Task DuplicateBlockInTailRecordDoesNotRefuseThePass()
    {
        // Same B1 tail guard as system-reminder-dedup: a ≥1KB dup landing
        // exactly in the tail record must not poison the whole pass.
        string doc = "PROJECT RULES\n" + new string('d', 1500);
        var b = new TranscriptBuilder()
            .UserPrompt("look")
            .AssistantText(doc)
            .UserPrompt("again")
            .AssistantText(doc)
            .UserPrompt("once more")
            .AssistantText(doc); // file ends on the third copy
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        string text = AllText(path);
        // The second copy is stubbed (pass NOT refused); first and tail stay.
        await Assert.That(text.Split(new string('d', 1500)).Length - 1).IsEqualTo(2);
        await Assert.That(text).Contains("duplicate content removed");
    }

    // ---- tool-result-age ----

    [Test]
    public async Task OldToolResultsBecomeStubsMidAgeGetTrimmed()
    {
        // Distinct outputs per read — identical ones would (correctly) be caught
        // by document-dedup before the age rule ever sees them.
        string OldOutput(string tag) => string.Join("\n",
            Enumerable.Range(1, 300).Select(i => $"{tag} line {i} " + new string('x', 40)));
        var b = new TranscriptBuilder().UserPrompt("start");
        // Each fat call is FOLLOWED by an unanswered tool_use in the same turn, which
        // trips chain-collapse's in-flight guard and makes the turn structurally
        // uncollapsible — so the age tiers are what act on these results, which is what
        // this test is about. Without it chain-collapse legitimately claims the turn
        // first (its economics gate admits single-call turns when the result is fat),
        // and no payload can satisfy both rules: the age trim only fires when it
        // SHRINKS the content (ToolResultAgeRule.Rewrite's length check), which needs a
        // wide payload, and any payload wide enough to trim is also worth collapsing.
        b.BashRead("sed -n '1,5p' unique-old.txt", out string oldId, OldOutput("old"));
        b.ToolUse("in-flight probe", out _, out _);
        AgeBy(b, AgeIndex.MidAgeTurns + 5); // now mid-age
        b.BashRead("sed -n '1,5p' unique-mid.txt", out string midId, OldOutput("mid"));
        b.ToolUse("in-flight probe", out _, out _);
        AgeBy(b, AgeIndex.OldAgeTurns - AgeIndex.MidAgeTurns); // first is now old
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        string? ContentOf(string id) => records
            .Select(r => (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(x => x["tool_use_id"]?.GetValue<string>() == id))
            .FirstOrDefault(x => x is not null)?["content"]?.GetValue<string>();

        string oldContent = ContentOf(oldId)!;
        await Assert.That(oldContent).StartsWith("[claudinine");       // old tier: stub with tool info
        await Assert.That(oldContent).Contains("Bash");
        await Assert.That(oldContent).Contains("lines,");

        string midContent = ContentOf(midId)!;
        await Assert.That(midContent).Contains("lines trimmed by claudinine"); // mid tier: head/tail trim
        await Assert.That(midContent).StartsWith("mid line 1 ");
        await Assert.That(midContent).EndsWith(new string('x', 40));
    }

    [Test]
    public async Task OldTierStubNamesToolAcrossLargeParallelBatch()
    {
        // Batch format: each use is its OWN record and results arrive in
        // completion order — answer the FIRST use LAST, so its use sits ~23
        // records before its result, far past the old fixed 10-record lookback
        // that produced anonymous "[claudinine — N lines…]" stubs.
        var b = new TranscriptBuilder().UserPrompt("do the batch");
        var uses = new List<(string Id, string Uuid)>();
        for (int i = 0; i < 12; i++)
        {
            b.ToolUse($"sed -n '1,5p' batch{i}.txt", out string id, out string uuid);
            uses.Add((id, uuid));
        }
        for (int i = 11; i >= 1; i--)
            b.ToolResultFor(uses[i].Id, uses[i].Uuid, $"short {i}");
        string bigOutput = string.Join("\n", Enumerable.Range(1, 200).Select(j => $"batch0 line {j}"));
        b.ToolResultFor(uses[0].Id, uses[0].Uuid, bigOutput);
        AgeBy(b, AgeIndex.OldAgeTurns + 5);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        // The rule alone: through the full catalog, chain-collapse would consume
        // the batch first and the age rule would never see these results.
        var transcript = Claudinine.Transcript.TranscriptFile.TryLoad(path)!;
        new ToolResultAgeRule().Apply(transcript);

        string stub = transcript.Records
            .Where(r => r.Replacement is not null)
            .SelectMany(r => RuleHelpers.ContentBlocks(r.Replacement!).OfType<JsonObject>())
            .Single(x => x["tool_use_id"]?.GetValue<string>() == uses[0].Id)
            ["content"]!.GetValue<string>();
        await Assert.That(stub).StartsWith("[claudinine: Bash"); // named, not anonymous
        await Assert.That(stub).Contains("batch0.txt");
    }

    /// <summary>Inner texts of an array-content tool_result, in block order.</summary>
    private static string[] ArrayTexts(string transcriptPath, string toolUseId) => Load(transcriptPath)
        .Select(r => (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(x => x["tool_use_id"]?.GetValue<string>() == toolUseId))
        .FirstOrDefault(x => x is not null)?["content"] is JsonArray parts
            ? [.. parts.OfType<JsonObject>().Select(p => p["text"]?.GetValue<string>() ?? "")]
            : [];

    [Test]
    public async Task McpArrayContentAgesLikeStringContent()
    {
        // MCP results carry a content ARRAY of text blocks; the pre-fix rule only
        // matched string content, so these escaped every tier (corpus: 332 blocks,
        // ~449K chars). Each big text block must age like a string payload; blocks
        // under the size floor stay verbatim.
        string Output(string tag) => string.Join("\n",
            Enumerable.Range(1, 300).Select(i => $"{tag} line {i} " + new string('x', 40)));
        var b = new TranscriptBuilder().UserPrompt("start");
        // Trailing unanswered call per turn, for the same reason as
        // OldToolResultsBecomeStubsMidAgeGetTrimmed: it trips chain-collapse's in-flight
        // guard so the age tiers are what rewrite these MCP array blocks.
        b.McpToolCall("mcp__grafana__query", out string oldId,
            Output("old"), "old meta " + new string('m', 200), "tiny meta");
        b.ToolUse("in-flight probe", out _, out _);
        AgeBy(b, AgeIndex.MidAgeTurns + 5);
        b.McpToolCall("mcp__grafana__query", out string midId, Output("mid"));
        b.ToolUse("in-flight probe", out _, out _);
        AgeBy(b, AgeIndex.OldAgeTurns - AgeIndex.MidAgeTurns);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        string[] oldTexts = ArrayTexts(path, oldId);
        await Assert.That(oldTexts[0]).StartsWith("[claudinine");           // old tier: stub
        await Assert.That(oldTexts[0]).Contains("mcp__grafana__query");     // named from the tool_use
        await Assert.That(oldTexts[1]).StartsWith("[claudinine");           // every big block stubs
        await Assert.That(oldTexts[2]).IsEqualTo("tiny meta");              // under MinContentChars: verbatim

        string[] midTexts = ArrayTexts(path, midId);
        await Assert.That(midTexts[0]).Contains("lines trimmed by claudinine"); // mid tier: head/tail trim
        await Assert.That(midTexts[0]).StartsWith("mid line 1 ");

        // Idempotence: a second full pass over own output changes nothing.
        string after = AllText(path);
        Compactor.Run(path);
        await Assert.That(AllText(path)).IsEqualTo(after);
    }

    [Test]
    public async Task OldMixedMediaResultComposesWithImageStrip()
    {
        // Age rule and image-strip rewrite the SAME record in one pass (the second
        // rule must clone the first's replacement, not the original): the big text
        // block gets the age stub, the image gets the retrieval stub.
        // Text above the age floor (100 chars) but the whole result — text PLUS the
        // base64 image, which the economics gate now prices — under chain-collapse's
        // break-even, so this single-call turn reaches the age and image rules instead
        // of being collapsed into a digest first.
        string bigText = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"page line {i}"));
        var b = new TranscriptBuilder().UserPrompt("look");
        b.ScreenshotToolCall(out string id, data: new byte[64], text: bigText);
        AgeBy(b, AgeIndex.OldAgeTurns + 5);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        string[] texts = ArrayTexts(path, id);
        await Assert.That(texts[0]).StartsWith("[claudinine");
        await Assert.That(texts[0]).Contains("computer");
        await Assert.That(texts[1]).Contains("--media"); // image-strip's retrieval stub survived
    }

    [Test]
    public async Task RecentToolResultsUntouched()
    {
        // Deliberately SMALL: this test is about the age tiers leaving a fresh result
        // alone, and a single fat call would now be collapsed by chain-collapse's
        // economics gate (correctly — one large result pays for its own digest),
        // which would mask what this asserts. Kept under the digest break-even so the
        // only rule that could touch it is an age rule.
        string bigOutput = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"line {i}"));
        var b = new TranscriptBuilder().UserPrompt("start");
        b.BashRead("sed -n '1,5p' fresh.txt", out _, bigOutput);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);
        string before = AllText(path);

        Compactor.Run(path);

        await Assert.That(AllText(path)).IsEqualTo(before);
    }

    // Real shape from the corpus: Claude Code overflows large tool output to a
    // sidecar under the session dir and leaves this stub. The absolute path is the
    // only pointer to that file — nothing ever garbage-collects it — so compaction
    // must never drop it.
    private static string PersistedOutput(string path) =>
        "<persisted-output>\nOutput too large (40.5KB). Full output saved to: " + path
        + "\n\nPreview (first 2KB):\n"
        + string.Join("\n", Enumerable.Range(1, 60).Select(i => $"preview line {i} " + new string('p', 30)));

    // AllText returns raw JSONL, where a Windows path is backslash-escaped — assert
    // on the decoded tool_result value instead of the wire bytes.
    private static string ResultContent(string transcriptPath, string toolUseId) => Load(transcriptPath)
        .Select(r => (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(x => x["tool_use_id"]?.GetValue<string>() == toolUseId))
        .FirstOrDefault(x => x is not null)?["content"]?.GetValue<string>() ?? "";

    [Test]
    public async Task OldPersistedOutputStubKeepsSidecarPath()
    {
        const string sidecar = @"C:\Users\u\.claude\projects\proj\sess\tool-results\byqro8ep6.txt";
        var b = new TranscriptBuilder().UserPrompt("start");
        b.BashRead("git diff", out string id, PersistedOutput(sidecar));
        AgeBy(b, AgeIndex.OldAgeTurns + 5);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        string content = ResultContent(path, id);
        await Assert.That(content).StartsWith("[claudinine");
        await Assert.That(content).Contains(sidecar);                  // pointer survives stubbing
        await Assert.That(content).DoesNotContain("preview line 42");  // preview itself still dropped
    }

    [Test]
    public async Task MidAgePersistedOutputLeftIntact()
    {
        // Trim keeps head/tail halves; a large enough preview could push the path
        // line out of both, so the mid tier must skip persisted-output blocks.
        const string sidecar = @"C:\Users\u\.claude\projects\proj\sess\tool-results\mid123.txt";
        string big = PersistedOutput(sidecar) + "\n"
            + string.Join("\n", Enumerable.Range(1, 400).Select(i => $"tail line {i} " + new string('t', 40)));
        var b = new TranscriptBuilder().UserPrompt("start");
        b.BashRead("git diff", out string id, big);
        AgeBy(b, AgeIndex.MidAgeTurns + 2);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        string content = ResultContent(path, id);
        await Assert.That(content).Contains(sidecar);
        await Assert.That(content).DoesNotContain("trimmed by claudinine");
    }

    [Test]
    public async Task CollapsedDigestPreviewKeepsSidecarPath()
    {
        // Sidecar refs live in multi-tool turns, so chain-collapse — not the age
        // rule — is what usually rewrites them. Its preview must carry the path.
        const string sidecar = @"C:\Users\u\.claude\projects\proj\sess\tool-results\bj0jrua4n.txt";
        string preview = PreviewRenderer.RenderPreview("Bash", "git diff", PersistedOutput(sidecar));
        await Assert.That(preview).Contains(sidecar);
        // A diff body full of "error:" must not outrank the path.
        string withError = PersistedOutput(sidecar) + "\nerror: something failed\n";
        await Assert.That(PreviewRenderer.RenderPreview("Bash", "git diff", withError)).Contains(sidecar);
    }

    [Test]
    public async Task PersistedOutputPathParsing()
    {
        await Assert.That(RuleHelpers.PersistedOutputPath(PersistedOutput(@"C:\a b\c.txt"))).IsEqualTo(@"C:\a b\c.txt");
        await Assert.That(RuleHelpers.PersistedOutputPath("ordinary tool output")).IsNull();
        // Guard against a malformed stub yielding an empty path.
        await Assert.That(RuleHelpers.PersistedOutputPath("<persisted-output>\nno marker here")).IsNull();
    }

    [Test]
    public async Task MidAgeJsonGetsMinified()
    {
        string prettyJson = "{\n" + string.Join(",\n", Enumerable.Range(1, 50)
            .Select(i => $"    \"key_{i}\"    :    \"value {i}\"")) + "\n}";
        await Assert.That(ToolResultAgeRule.Minify(prettyJson).Length < prettyJson.Length * 0.85).IsTrue();
    }

    // ---- mega-block-trim ----

    [Test]
    public async Task OldMegaTextBlockTrimmedRecentOneKept()
    {
        string mega = new string('m', MegaBlockTrimRule.MaxBlockBytes + 5000);
        var b = new TranscriptBuilder().UserPrompt("start").AssistantText(mega);
        AgeBy(b, AgeIndex.MidAgeTurns + 1);
        b.AssistantText(mega); // recent copy — must survive (and dodge document-dedup? different rule)
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        string text = AllText(path);
        await Assert.That(text).Contains("bytes trimmed by claudinine");
    }

    // ---- image-strip ----

    [Test]
    public async Task OldImagesStubbedRecentImagesKept()
    {
        // Pins both edges of the image clock (faster than the shared IsMidAged):
        // img-old sits exactly at the threshold, img-new exactly one turn under.
        var b = new TranscriptBuilder().UserPrompt("here is a screenshot");
        b.RawImageMessage("img-old");
        AgeBy(b, 1);
        b.RawImageMessage("img-new");
        AgeBy(b, AgeIndex.ImageAgeTurns - 1);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        var imageBlocks = records
            .SelectMany(r => (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Where(x => x["type"]?.GetValue<string>() == "image")
            .ToList();
        await Assert.That(imageBlocks).HasSingleItem(); // the recent one
        await Assert.That(AllText(path)).Contains("--media"); // the old one, stubbed with its retrieval pointer
    }

    [Test]
    public async Task ImageAgesByObservationCountNotTurns()
    {
        // One prompt, no further turns: the image must still age out once
        // ImageAgeResults observations have landed after it. Short reads stay
        // under chain-collapse's break-even, so image-strip sees the originals.
        var b = new TranscriptBuilder().UserPrompt("look at this screenshot");
        b.RawImageMessage("img");
        for (int i = 0; i < AgeIndex.ImageAgeResults; i++)
            b.BashRead($"sed -n '1,5p' f{i}.txt", out _, $"short {i}");
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        var imageBlocks = records
            .SelectMany(r => (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Where(x => x["type"]?.GetValue<string>() == "image")
            .ToList();
        await Assert.That(imageBlocks).IsEmpty();
        await Assert.That(AllText(path)).Contains("--media");
    }

    [Test]
    public async Task AgenticSessionAgesByObservationCountNotTurns()
    {
        // One user prompt, a marathon of tool calls: the turn clock never moves,
        // but results older than the observation window must still be stubbed.
        var b = new TranscriptBuilder().UserPrompt("do the big thing");
        b.BashRead("sed -n '1,5p' first.txt", out string firstId,
            string.Join("\n", Enumerable.Range(1, 200).Select(i => $"first {i}")));
        for (int i = 0; i < AgeIndex.OldAgeResults + 5; i++)
            b.BashRead($"sed -n '1,5p' other{i}.txt", out _, $"short {i}");
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        string content = records
            .Select(r => (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(x => x["tool_use_id"]?.GetValue<string>() == firstId))
            .First(x => x is not null)!["content"]!.GetValue<string>();
        await Assert.That(content).StartsWith("[claudinine");
    }

    // ---- cross-rule sanity ----

    [Test]
    public async Task FullCatalogPassIsIdempotent()
    {
        // Exercises EVERY tier: reminder dedup, doc dedup, image strip, old-tier
        // stubs, mid-tier trims (line path AND multibyte byte path), mega trim —
        // the mid-tier trims are the ones that once re-shaved their own tails.
        string bigLines = string.Join("\n", Enumerable.Range(1, 300).Select(i => $"line {i} " + new string('y', 30)));
        string bigMultibyte = string.Concat(Enumerable.Repeat("héllo wörld émoji 🎉 ", 600)); // one long line, >8KB utf8
        string mega = new string('m', MegaBlockTrimRule.MaxBlockBytes + 5000);
        string reminder = "<system-reminder>rule " + new string('r', 400) + "</system-reminder>";

        var b = new TranscriptBuilder().UserPrompt("start\n" + reminder);
        b.BashRead("sed -n '1,5p' a.txt", out _, bigLines);      // → old tier (stub)
        b.RawImageMessage("img1");
        b.AssistantText(mega);                                    // → mega trim
        AgeBy(b, AgeIndex.OldAgeTurns + 2);                       // everything above is old
        b.UserPrompt("mid\n" + reminder);
        b.BashRead("sed -n '1,5p' b.txt", out _, bigLines + " b"); // → mid tier (line trim)
        b.BashRead("sed -n '1,5p' c.txt", out _, bigMultibyte);   // → mid tier (byte trim, multibyte)
        AgeBy(b, AgeIndex.MidAgeTurns + 1);                       // mid block now mid-aged
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);
        string afterFirst = AllText(path);
        await Assert.That(afterFirst).Contains("trimmed by claudinine"); // trims actually happened
        Compactor.Run(path);
        await Assert.That(AllText(path)).IsEqualTo(afterFirst);
        Compactor.Run(path); // and stays fixed
        await Assert.That(AllText(path)).IsEqualTo(afterFirst);
    }
}
