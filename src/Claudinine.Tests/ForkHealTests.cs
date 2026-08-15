namespace Claudinine.Tests;

/// <summary>
/// Post-fork mirror heal: the desktop can fork a conversation to a NEW session id
/// (same record uuids, new sessionId stamped, old jsonl orphaned). The fork's
/// digests still name the parent session; the fork's mirror lacks the pre-fork
/// originals. ForkHealRule merges the parent mirror in and retargets the digests.
/// </summary>
public sealed class ForkHealTests : IDisposable
{
    private readonly string _dir;
    // These tests need the parent to actually CARRY digests, so the fixture payload
    // has to clear chain-collapse's economics gate (digest must beat the bytes it
    // replaces). 500b did not; see ChainCollapseTests.Output for the corpus sizing.
    private static readonly string LongOutput = new('x', 2000);

    public ForkHealTests()
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

    /// <summary>
    /// Compact a real multi-call session ("test-session"), then simulate the
    /// desktop fork: copy the compacted file under a new id, restamping ONLY the
    /// sessionId field — digest text keeps naming the parent, exactly as observed.
    /// </summary>
    private async Task<(string ForkPath, string ParentPath)> BuildForkedSession()
    {
        string parentPath;
        var b = new TranscriptBuilder().UserPrompt("investigate");
        b.BashRead("sed -n '1,50p' src/a.cs", out _, LongOutput);
        b.BashRead("sed -n '1,50p' src/b.cs", out _, LongOutput + "b");
        b.AssistantText("done");
        parentPath = b.WriteTo(_dir);
        Compactor.Run(parentPath);
        await Assert.That(File.ReadAllText(parentPath)).Contains("claudinine get test-session");

        string forkPath = Path.Combine(_dir, "fork-session.jsonl");
        var lines = File.ReadAllLines(parentPath).Select(l =>
        {
            var node = (JsonObject)JsonNode.Parse(l)!;
            if (node["sessionId"] is not null)
                node["sessionId"] = "fork-session";
            return node.ToJsonString();
        });
        File.WriteAllText(forkPath, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        return (forkPath, parentPath);
    }

    private static HashSet<string> MirrorUuids(string mirrorPath) =>
        File.ReadAllLines(mirrorPath).Skip(1).Where(l => l.Length > 0)
            .Select(l => ((JsonObject)JsonNode.Parse(l)!)["uuid"]?.GetValue<string>())
            .OfType<string>().ToHashSet();

    [Test]
    public async Task ForkAdoptsParentMirrorAndRetargetsDigests()
    {
        (string forkPath, string parentPath) = await BuildForkedSession();

        Compactor.Run(forkPath);

        // Digest refs now point at the fork's own session id.
        string text = File.ReadAllText(forkPath);
        await Assert.That(text).DoesNotContain("claudinine get test-session");
        await Assert.That(text).Contains("claudinine get fork-session");

        // The fork's mirror holds everything the parent mirror held — including
        // the interior records chain-collapse removed, which exist NOWHERE else.
        var parentUuids = MirrorUuids(MirrorLocator.PathFor(parentPath));
        var forkUuids = MirrorUuids(MirrorLocator.PathFor(forkPath));
        await Assert.That(parentUuids.IsSubsetOf(forkUuids)).IsTrue();
        var transcriptUuids = File.ReadAllLines(forkPath)
            .Select(l => ((JsonObject)JsonNode.Parse(l)!)["uuid"]?.GetValue<string>())
            .OfType<string>().ToHashSet();
        await Assert.That(parentUuids).Contains(u => !transcriptUuids.Contains(u));

        // Merged records read as the fork's own history.
        foreach (string line in File.ReadAllLines(MirrorLocator.PathFor(forkPath)).Skip(1))
        {
            var node = (JsonObject)JsonNode.Parse(line)!;
            if (node["sessionId"] is not null)
                await Assert.That(node["sessionId"]!.GetValue<string>()).IsEqualTo("fork-session");
        }

        // Parent transcript and mirror untouched.
        await Assert.That(File.ReadAllText(parentPath)).Contains("claudinine get test-session");
        await Assert.That(File.Exists(MirrorLocator.PathFor(parentPath))).IsTrue();
    }

    [Test]
    public async Task MissingParentMirrorLeavesDigestsUntouched()
    {
        (string forkPath, string parentPath) = await BuildForkedSession();
        File.Delete(MirrorLocator.PathFor(parentPath));

        Compactor.Run(forkPath);

        // Fail-closed: nothing to merge from → refs keep naming the parent (they
        // are dead either way; retargeting would only mask that).
        await Assert.That(File.ReadAllText(forkPath)).Contains("claudinine get test-session");
        await Assert.That(File.ReadAllText(forkPath)).DoesNotContain("claudinine get fork-session");
    }

    [Test]
    public async Task HealIsIdempotent()
    {
        (string forkPath, _) = await BuildForkedSession();
        Compactor.Run(forkPath);
        string afterFirst = File.ReadAllText(forkPath);
        string mirrorAfterFirst = File.ReadAllText(MirrorLocator.PathFor(forkPath));

        Compactor.Run(forkPath);

        await Assert.That(File.ReadAllText(forkPath)).IsEqualTo(afterFirst);
        await Assert.That(File.ReadAllText(MirrorLocator.PathFor(forkPath))).IsEqualTo(mirrorAfterFirst);
    }

    [Test]
    public async Task ProseQuotingAnotherSessionsCommandIsNotRewritten()
    {
        // Dev sessions quote retrieval commands in ordinary prose. Only records
        // carrying our claudinine marker are retargeted.
        string quote = "run claudinine get test-session --ref abc123de --full to see it";
        var b = new TranscriptBuilder().UserPrompt("hello");
        b.AssistantText(quote);
        string path = b.WriteTo(_dir, "fork-session.jsonl");

        // A parent mirror exists, so a heal WOULD fire if the record were marked.
        WriteMirror("test-session", ("11111111-0000-0000-0000-000000000001", "original"));

        var transcript = Claudinine.Transcript.TranscriptFile.TryLoad(path)!;
        new ForkHealRule().Apply(transcript);

        foreach (var r in transcript.Records)
            await Assert.That(r.Replacement).IsNull();
    }

    [Test]
    public async Task TailMarkedRecordStillMergesButIsNotRewritten()
    {
        // An interrupted fork can end exactly at a marked carrier. The mirror
        // merge must still happen (it is pure gain), but the tail record itself
        // is never replaced — TryRewrite would refuse the whole pass.
        string carrier = MarkedCarrier("claudinine get test-session --ref REF", "fork-session");
        var b = new TranscriptBuilder().UserPrompt("hello");
        string path = b.RawLine(carrier).WriteTo(_dir, "fork-session.jsonl");
        // The carrier's own uuid is in the parent mirror: a genuine fork copy.
        WriteMirror("test-session",
            (CarrierUuid, "original"),
            ("11111111-0000-0000-0000-000000000001", "removed interior"));

        var transcript = Claudinine.Transcript.TranscriptFile.TryLoad(path)!;
        new ForkHealRule().Apply(transcript);

        foreach (var r in transcript.Records)
            await Assert.That(r.Replacement).IsNull();
        await Assert.That(transcript.TryRewrite()).IsTrue();
        await Assert.That(MirrorUuids(MirrorLocator.PathFor(path))).Contains("11111111-0000-0000-0000-000000000001");
    }

    [Test]
    public async Task QuotedCommandInsideACarrierIsNotAForkParent()
    {
        // Dev sessions run `claudinine get <other-session>` via Bash; the command
        // ends up quoted inside their OWN carriers' preview lines. The quoted
        // session's mirror exists, but this carrier's uuid is not in it — no
        // merge, no rewrite: the quote is a historical report, not a fork ref.
        string carrier = MarkedCarrier(
            "[abc12345] Bash(claudinine get test-session --ref d5963f0f --info) -> 42b :: [d5963f0f] 120 bytes",
            "fork-session");
        var b = new TranscriptBuilder().UserPrompt("hello");
        b.RawLine(carrier);
        b.AssistantText("done");
        string path = b.WriteTo(_dir, "fork-session.jsonl");
        WriteMirror("test-session", ("11111111-0000-0000-0000-000000000001", "unrelated"));
        string before = File.ReadAllText(path);

        Compactor.Run(path);

        await Assert.That(File.ReadAllText(path)).Contains("claudinine get test-session");
        var forkUuids = MirrorUuids(MirrorLocator.PathFor(path));
        await Assert.That(forkUuids).DoesNotContain("11111111-0000-0000-0000-000000000001");
    }

    [Test]
    public async Task PlaceholderSessionIdIsIgnored()
    {
        // ChainCollapseRule writes "<session-id>" when a record carries no
        // sessionId; that must never read as a fork parent.
        string carrier = MarkedCarrier("claudinine get <session-id> --ref REF", "fork-session");
        var b = new TranscriptBuilder().UserPrompt("hello");
        b.RawLine(carrier);
        b.AssistantText("done");
        string path = b.WriteTo(_dir, "fork-session.jsonl");
        string before = File.ReadAllText(path);

        var transcript = Claudinine.Transcript.TranscriptFile.TryLoad(path)!;
        new ForkHealRule().Apply(transcript);
        await Assert.That(transcript.TryRewrite()).IsTrue();

        await Assert.That(File.ReadAllText(path)).IsEqualTo(before);
    }

    private const string CarrierUuid = "99999999-0000-0000-0000-000000000099";

    /// <summary>A minimal chain-collapse-style carrier bearing our marker.</summary>
    private static string MarkedCarrier(string digestText, string sessionId) =>
        new JsonObject
        {
            ["type"] = "user",
            ["uuid"] = CarrierUuid,
            ["parentUuid"] = "00000000-0000-0000-0000-000000000001",
            ["sessionId"] = sessionId,
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = "toolu_0001",
                    ["content"] = $"[claudinine: this turn originally ran 2 separate tool calls. {digestText}]",
                }),
            },
            ["claudinine"] = new JsonObject
            {
                ["v"] = 1,
                ["rule"] = "chain-collapse",
                ["origUuid"] = CarrierUuid,
            },
        }.ToJsonString();

    private void WriteMirror(string sessionId, params (string Uuid, string Content)[] records)
    {
        // The parent's mirror goes into the LEGACY env pool deliberately: fork
        // healing must keep finding pre-migration parents there.
        string dir = Path.Combine(
            Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA")!, "mirrors");
        Directory.CreateDirectory(dir);
        var lines = new List<string>
        {
            new JsonObject
            {
                ["claudinine"] = new JsonObject
                {
                    ["v"] = "1",
                    ["mirrorOf"] = sessionId + ".jsonl",
                },
            }.ToJsonString(),
        };
        foreach ((string uuid, string content) in records)
        {
            lines.Add(new JsonObject
            {
                ["type"] = "user",
                ["uuid"] = uuid,
                ["sessionId"] = sessionId,
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
            }.ToJsonString());
        }
        File.WriteAllText(Path.Combine(dir, sessionId + ".jsonl"),
            string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    }
}
