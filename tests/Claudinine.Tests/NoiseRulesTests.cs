using System.Text.Json.Nodes;
using Claudinine.Rules;
using Xunit;

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

    [Fact]
    public void DuplicateSystemRemindersRemovedFirstKept()
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
        Assert.Equal(1, occurrences); // only the first copy survives
    }

    [Fact]
    public void DuplicateReminderWithinOneMessageKeepsFirst()
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
        Assert.Equal(1, text.Split("always use tabs").Length - 1);
    }

    // ---- document-dedup ----

    [Fact]
    public void LargeDuplicateBlocksStubbedAfterFirst()
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
        Assert.Equal(1, text.Split(new string('d', 1500)).Length - 1);
        Assert.Contains("duplicate content removed", text);
        Assert.Contains("first seen earlier: PROJECT RULES", text);
    }

    // ---- tool-result-age ----

    [Fact]
    public void OldToolResultsBecomeStubsMidAgeGetTrimmed()
    {
        // Distinct outputs per read — identical ones would (correctly) be caught
        // by document-dedup before the age rule ever sees them.
        string OldOutput(string tag) => string.Join("\n",
            Enumerable.Range(1, 300).Select(i => $"{tag} line {i} " + new string('x', 40)));
        var b = new TranscriptBuilder().UserPrompt("start");
        b.BashRead("sed -n '1,5p' unique-old.txt", out string oldId, OldOutput("old"));
        AgeBy(b, AgeIndex.MidAgeTurns + 5); // now mid-age
        b.BashRead("sed -n '1,5p' unique-mid.txt", out string midId, OldOutput("mid"));
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
        Assert.StartsWith("[claudinine", oldContent);       // old tier: stub with tool info
        Assert.Contains("Bash", oldContent);
        Assert.Contains("lines,", oldContent);

        string midContent = ContentOf(midId)!;
        Assert.Contains("lines trimmed by claudinine", midContent); // mid tier: head/tail trim
        Assert.StartsWith("mid line 1 ", midContent);
        Assert.EndsWith(new string('x', 40), midContent);
    }

    [Fact]
    public void RecentToolResultsUntouched()
    {
        string bigOutput = string.Join("\n", Enumerable.Range(1, 300).Select(i => $"line {i}"));
        var b = new TranscriptBuilder().UserPrompt("start");
        b.BashRead("sed -n '1,5p' fresh.txt", out _, bigOutput);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);
        string before = AllText(path);

        Compactor.Run(path);

        Assert.Equal(before, AllText(path));
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

    [Fact]
    public void OldPersistedOutputStubKeepsSidecarPath()
    {
        const string sidecar = @"C:\Users\u\.claude\projects\proj\sess\tool-results\byqro8ep6.txt";
        var b = new TranscriptBuilder().UserPrompt("start");
        b.BashRead("git diff", out string id, PersistedOutput(sidecar));
        AgeBy(b, AgeIndex.OldAgeTurns + 5);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        string content = ResultContent(path, id);
        Assert.StartsWith("[claudinine", content);
        Assert.Contains(sidecar, content);                  // pointer survives stubbing
        Assert.DoesNotContain("preview line 42", content);  // preview itself still dropped
    }

    [Fact]
    public void MidAgePersistedOutputLeftIntact()
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
        Assert.Contains(sidecar, content);
        Assert.DoesNotContain("trimmed by claudinine", content);
    }

    [Fact]
    public void CollapsedDigestPreviewKeepsSidecarPath()
    {
        // Sidecar refs live in multi-tool turns, so chain-collapse — not the age
        // rule — is what usually rewrites them. Its preview must carry the path.
        const string sidecar = @"C:\Users\u\.claude\projects\proj\sess\tool-results\bj0jrua4n.txt";
        string preview = PreviewRenderer.RenderPreview("Bash", "git diff", PersistedOutput(sidecar));
        Assert.Contains(sidecar, preview);
        // A diff body full of "error:" must not outrank the path.
        string withError = PersistedOutput(sidecar) + "\nerror: something failed\n";
        Assert.Contains(sidecar, PreviewRenderer.RenderPreview("Bash", "git diff", withError));
    }

    [Fact]
    public void PersistedOutputPathParsing()
    {
        Assert.Equal(@"C:\a b\c.txt",
            RuleHelpers.PersistedOutputPath(PersistedOutput(@"C:\a b\c.txt")));
        Assert.Null(RuleHelpers.PersistedOutputPath("ordinary tool output"));
        // Guard against a malformed stub yielding an empty path.
        Assert.Null(RuleHelpers.PersistedOutputPath("<persisted-output>\nno marker here"));
    }

    [Fact]
    public void MidAgeJsonGetsMinified()
    {
        string prettyJson = "{\n" + string.Join(",\n", Enumerable.Range(1, 50)
            .Select(i => $"    \"key_{i}\"    :    \"value {i}\"")) + "\n}";
        Assert.True(ToolResultAgeRule.Minify(prettyJson).Length < prettyJson.Length * 0.85);
    }

    // ---- mega-block-trim ----

    [Fact]
    public void OldMegaTextBlockTrimmedRecentOneKept()
    {
        string mega = new string('m', MegaBlockTrimRule.MaxBlockBytes + 5000);
        var b = new TranscriptBuilder().UserPrompt("start").AssistantText(mega);
        AgeBy(b, AgeIndex.MidAgeTurns + 1);
        b.AssistantText(mega); // recent copy — must survive (and dodge document-dedup? different rule)
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        string text = AllText(path);
        Assert.Contains("bytes trimmed by claudinine", text);
    }

    // ---- image-strip ----

    [Fact]
    public void OldImagesStubbedRecentImagesKept()
    {
        var b = new TranscriptBuilder().UserPrompt("here is a screenshot");
        b.RawImageMessage("img-old");
        AgeBy(b, AgeIndex.MidAgeTurns + 1);
        b.RawImageMessage("img-new");
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        var imageBlocks = records
            .SelectMany(r => (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Where(x => x["type"]?.GetValue<string>() == "image")
            .ToList();
        Assert.Single(imageBlocks); // the recent one
        Assert.Contains("--media", AllText(path)); // the old one, stubbed with its retrieval pointer
    }

    [Fact]
    public void AgenticSessionAgesByObservationCountNotTurns()
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
        Assert.StartsWith("[claudinine", content);
    }

    // ---- cross-rule sanity ----

    [Fact]
    public void FullCatalogPassIsIdempotent()
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
        Assert.Contains("trimmed by claudinine", afterFirst); // trims actually happened
        Compactor.Run(path);
        Assert.Equal(afterFirst, AllText(path));
        Compactor.Run(path); // and stays fixed
        Assert.Equal(afterFirst, AllText(path));
    }
}
