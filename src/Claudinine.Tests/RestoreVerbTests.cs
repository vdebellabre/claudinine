using System.Text.Json;

namespace Claudinine.Tests;

/// <summary>
/// The restore loop: `restore-compaction-off` rebuilds the transcript from the
/// mirror and freezes the session (skip marker — hooks mirror but never compact);
/// `restore-compaction-on` restores once and lets steady-state compaction resume,
/// doubling as the re-enable action.
/// </summary>
public sealed class RestoreVerbTests : IDisposable
{
    private readonly string _dir;

    public RestoreVerbTests()
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

    private string MirrorsDir => Path.Combine(_dir, "plugin-data", "mirrors");

    /// <summary>A session rich enough that several rules fire.</summary>
    private string BuildCompactableSession()
    {
        // Corpus-sized: see ChainCollapseTests.Output — chain-collapse only fires when
        // the digest beats the payload it replaces, so 412b would leave this session
        // uncompacted and the restore path untested.
        string output = "tool output " + new string('o', 2000);
        var b = new TranscriptBuilder().UserPrompt("do the thing");
        for (int i = 0; i < 3; i++)
            b.ToolCall("Bash", new JsonObject { ["command"] = $"echo step {i}" }, output + i);
        b.AssistantText("half-way");
        b.RawImageMessage("m1");
        for (int i = 0; i < 20; i++)
            b.UserPrompt($"turn filler {i}").AssistantText("ok");
        b.AssistantText("done");
        return b.WriteTo(_dir);
    }

    /// <summary>
    /// No output assertions here — only the exit code matters. The verb's chatter
    /// goes to TUnit's per-test capture, which is reported only when a test fails.
    /// </summary>
    private static int RunRestore(string[] args, bool compactionOn) =>
        RestoreVerb.Run(args, compactionOn);

    private static void RunHook(string transcriptPath, string eventName = "UserPromptSubmit")
    {
        string json = JsonSerializer.Serialize(new
        {
            hook_event_name = eventName,
            transcript_path = transcriptPath,
            session_id = Path.GetFileNameWithoutExtension(transcriptPath),
        });
        HookRunner.Run(new MemoryStream(Encoding.UTF8.GetBytes(json)));
    }

    // ---- ReadMirrors: cross-mirror merge. Unit seam on purpose — a real
    // two-dir layout can't be reproduced end-to-end, because the home-based
    // search directories aren't redirectable in tests (registry-backed on
    // Windows), and this multiplicity logic is the subtlest code in the verb. ----

    private string WriteRawMirror(string name, params string[] bodyLines)
    {
        Directory.CreateDirectory(MirrorsDir);
        string path = Path.Combine(MirrorsDir, name);
        const string header = """{"claudinine":{"v":"1","mirrorOf":"C:\\somewhere.jsonl"}}""";
        File.WriteAllText(path,
            string.Join("\n", new[] { header }.Concat(bodyLines)) + "\n", new UTF8Encoding(false));
        return path;
    }

    private const string QueueOp =
        """{"type":"queue-operation","operation":"enqueue","content":"x","sessionId":"s"}""";

    [Test]
    public async Task UuidlessLinesMergeByMaxMultiplicityAcrossMirrors()
    {
        // Cross-context copies of the SAME session can hold the same uuid-less
        // line a different number of times; the restore must reproduce the
        // maximum — neither the sum (5) nor the first file's count (2).
        string a = WriteRawMirror("a.jsonl", QueueOp, QueueOp);
        string b = WriteRawMirror("b.jsonl", QueueOp, QueueOp, QueueOp);

        (List<RestoreVerb.Line> lines, bool forkMerged) = RestoreVerb.ReadMirrors([a, b]);

        await Assert.That(forkMerged).IsFalse();
        await Assert.That(lines.Count(l => l.Raw == QueueOp)).IsEqualTo(3);
    }

    [Test]
    public async Task MultiplicityMergeIsOrderIndependent()
    {
        string a = WriteRawMirror("a.jsonl", QueueOp, QueueOp);
        string b = WriteRawMirror("b.jsonl", QueueOp, QueueOp, QueueOp);

        // Larger file first: the smaller one must contribute nothing new.
        (List<RestoreVerb.Line> lines, _) = RestoreVerb.ReadMirrors([b, a]);

        await Assert.That(lines.Count(l => l.Raw == QueueOp)).IsEqualTo(3);
    }

    [Test]
    public async Task UuidRecordsDedupAcrossMirrorsFirstFileWins()
    {
        string a = WriteRawMirror("a.jsonl",
            """{"type":"user","uuid":"u1","note":"from-a"}""");
        string b = WriteRawMirror("b.jsonl",
            """{"type":"user","uuid":"u1","note":"from-b"}""",
            """{"type":"user","uuid":"u2","note":"only-b"}""");

        (List<RestoreVerb.Line> lines, _) = RestoreVerb.ReadMirrors([a, b]);

        RestoreVerb.Line u1 = await Assert.That(lines).HasSingleItem(l => l.Uuid == "u1");
        await Assert.That(u1.Raw).Contains("from-a");
        await Assert.That(lines).HasSingleItem(l => l.Uuid == "u2");
    }

    [Test]
    public async Task ForkSeparatorSetsFlagButIsNotARecord()
    {
        string a = WriteRawMirror("a.jsonl",
            """{"type":"user","uuid":"u1"}""",
            """{"claudinine":{"v":"1","mergedFromFork":"parent-sid"}}""",
            """{"type":"user","uuid":"u0"}""");

        (List<RestoreVerb.Line> lines, bool forkMerged) = RestoreVerb.ReadMirrors([a]);

        await Assert.That(forkMerged).IsTrue();
        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines).DoesNotContain(l => l.Raw.Contains("mergedFromFork"));
    }

    [Test]
    public async Task RestoreRoundTripsToTheExactOriginal()
    {
        string path = BuildCompactableSession();
        string original = File.ReadAllText(path);

        Compactor.Run(path);
        await Assert.That(File.ReadAllText(path)).IsNotEqualTo(original); // compaction did something

        int rc = RunRestore(["test-session"], compactionOn: true);
        await Assert.That(rc).IsEqualTo(0);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(original); // byte-identical restore
    }

    [Test]
    public async Task RestoreOffFreezesTheSession()
    {
        string path = BuildCompactableSession();
        Compactor.Run(path);

        await Assert.That(RunRestore(["test-session"], compactionOn: false)).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(MirrorsDir, "test-session.skip"))).IsTrue();
        string restored = File.ReadAllText(path);

        // Hooks now mirror but never compact: the restored file survives a pass.
        RunHook(path);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(restored);
        RunHook(path, "SessionEnd");
        await Assert.That(File.ReadAllText(path)).IsEqualTo(restored);
    }

    [Test]
    public async Task FrozenSessionStillGetsMirrored()
    {
        string path = BuildCompactableSession();
        Compactor.Run(path);
        await Assert.That(RunRestore(["test-session"], compactionOn: false)).IsEqualTo(0);

        // New turns land while frozen…
        var extra = new TranscriptBuilder().UserPrompt("post-freeze work");
        string extraPath = extra.WriteTo(_dir, "extra.jsonl");
        string newRecord = File.ReadAllLines(extraPath)[0]
            .Replace("00000000-0000-0000-0000-000000000001", "99999999-0000-0000-0000-000000000001");
        File.AppendAllText(path, newRecord + "\n");

        RunHook(path);

        // …and are mirrored despite compaction being off.
        string mirror = File.ReadAllText(Path.Combine(MirrorsDir, "test-session.jsonl"));
        await Assert.That(mirror).Contains("99999999-0000-0000-0000-000000000001");
        await Assert.That(File.ReadAllText(path)).Contains("post-freeze work"); // transcript untouched
    }

    [Test]
    public async Task RestoreOnUnfreezesAndCompactionResumes()
    {
        string path = BuildCompactableSession();
        Compactor.Run(path);
        await Assert.That(RunRestore(["test-session"], compactionOn: false)).IsEqualTo(0);

        await Assert.That(RunRestore(["test-session"], compactionOn: true)).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(MirrorsDir, "test-session.skip"))).IsFalse();

        string restored = File.ReadAllText(path);
        RunHook(path);
        await Assert.That(File.ReadAllText(path)).IsNotEqualTo(restored); // compaction is back
    }

    [Test]
    public async Task UnmirroredTailIsCapturedBeforeRestoring()
    {
        string path = BuildCompactableSession();
        Compactor.Run(path);

        // Simulate a crash: records the mirror has never seen sit at the tail.
        string[] lines = File.ReadAllLines(path);
        var lastRec = (JsonObject)JsonNode.Parse(lines[^1])!;
        var crashRec = new JsonObject
        {
            ["type"] = "user",
            ["uuid"] = "cccccccc-0000-0000-0000-000000000001",
            ["parentUuid"] = lastRec["uuid"]!.GetValue<string>(),
            ["sessionId"] = "test-session",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = "crash-tail prompt" },
        };
        File.AppendAllText(path, crashRec.ToJsonString() + "\n");

        await Assert.That(RunRestore(["test-session"], compactionOn: true)).IsEqualTo(0);
        string restored = File.ReadAllText(path);
        await Assert.That(restored).Contains("crash-tail prompt");
        await Assert.That(((JsonObject)JsonNode.Parse(File.ReadAllLines(path)[^1])!)["uuid"]!.GetValue<string>()).EndsWith("cccccccc-0000-0000-0000-000000000001");
    }

    [Test]
    public async Task RestoreRefusedWhenMirrorMissesALiveRecord()
    {
        string path = BuildCompactableSession();
        Compactor.Run(path);
        string compacted = File.ReadAllText(path);

        // Corrupt the mirror: drop the original of a MARKED live record. (An
        // unmarked one would be re-appended by the pre-restore mirror sync —
        // self-healing, not a corruption case.)
        string mirrorPath = Path.Combine(MirrorsDir, "test-session.jsonl");
        string[] mirrorLines = File.ReadAllLines(mirrorPath);
        string victimUuid = File.ReadAllLines(path)
            .Select(l => (JsonObject)JsonNode.Parse(l)!)
            .First(r => r["claudinine"] is not null && r["uuid"] is not null)
            ["uuid"]!.GetValue<string>();
        File.WriteAllText(mirrorPath, string.Join("\n",
            mirrorLines.Where(l => !l.Contains(victimUuid))) + "\n");

        await Assert.That(RunRestore(["test-session"], compactionOn: true)).IsEqualTo(1);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(compacted); // fail-closed: untouched
    }

    [Test]
    public async Task ForkHealedMirrorIsReorderedParentFirst()
    {
        // A healed fork's mirror holds the fork's own records first and the
        // parent's pre-fork originals appended at the END; the restore must put
        // parents back before the records that chain onto them.
        string path = new TranscriptBuilder().UserPrompt("fork continues").WriteTo(_dir);
        var live = (JsonObject)JsonNode.Parse(File.ReadAllLines(path)[0])!;
        live["parentUuid"] = "aaaaaaaa-0000-0000-0000-000000000002";
        File.WriteAllText(path, live.ToJsonString() + "\n");

        var header = new JsonObject
        {
            ["claudinine"] = new JsonObject { ["v"] = "1", ["mirrorOf"] = Path.GetFullPath(path) },
        };
        JsonObject Rec(string uuid, string? parent, string text) => new()
        {
            ["type"] = "user",
            ["uuid"] = uuid,
            ["parentUuid"] = parent,
            ["sessionId"] = "test-session",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
        };
        var separator = new JsonObject
        {
            ["claudinine"] = new JsonObject { ["v"] = "1", ["mergedFromFork"] = "parent-session" },
        };
        Directory.CreateDirectory(MirrorsDir);
        File.WriteAllText(Path.Combine(MirrorsDir, "test-session.jsonl"), string.Join("\n",
        [
            header.ToJsonString(),
            live.ToJsonString(),                                                     // fork's own record
            separator.ToJsonString(),                                                // heal marker
            Rec("aaaaaaaa-0000-0000-0000-000000000001", null, "pre-fork 1").ToJsonString(),      // merged parent
            Rec("aaaaaaaa-0000-0000-0000-000000000002",
                "aaaaaaaa-0000-0000-0000-000000000001", "pre-fork 2").ToJsonString(),
        ]) + "\n");

        await Assert.That(RunRestore(["test-session"], compactionOn: true)).IsEqualTo(0);

        string[] restored = File.ReadAllLines(path);
        await Assert.That(restored.Length).IsEqualTo(3);
        await Assert.That(restored[0]).Contains("pre-fork 1");
        await Assert.That(restored[1]).Contains("pre-fork 2");
        await Assert.That(restored[2]).Contains("fork continues"); // live tail preserved as tail
    }

    [Test]
    public async Task AppWriteQuirkOrderIsPreservedVerbatim()
    {
        // Real app files can hold a tool_result a couple of lines BEFORE its
        // parent use record (batch write race, observed on the 2026-08 corpus).
        // Without a fork-heal separator, mirror order IS file order and must be
        // reproduced exactly — a chain-aware "fix" would falsify the file.
        JsonObject Rec(string uuid, string? parent, string text) => new()
        {
            ["type"] = "user",
            ["uuid"] = uuid,
            ["parentUuid"] = parent,
            ["sessionId"] = "test-session",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
        };
        string path = Path.Combine(_dir, "test-session.jsonl");
        File.WriteAllText(path, string.Join("\n",
        [
            Rec("bbbbbbbb-0000-0000-0000-000000000001", null, "root").ToJsonString(),
            Rec("bbbbbbbb-0000-0000-0000-000000000003",
                "bbbbbbbb-0000-0000-0000-000000000002", "child written early").ToJsonString(),
            Rec("bbbbbbbb-0000-0000-0000-000000000002",
                "bbbbbbbb-0000-0000-0000-000000000001", "parent written late").ToJsonString(),
        ]) + "\n");
        string original = File.ReadAllText(path);

        Compactor.Run(path);
        await Assert.That(RunRestore(["test-session"], compactionOn: true)).IsEqualTo(0);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(original);
    }

    [Test]
    public async Task OrphanSkipMarkersAreCollected()
    {
        string path = BuildCompactableSession();
        Compactor.Run(path);
        await Assert.That(RunRestore(["test-session"], compactionOn: false)).IsEqualTo(0);
        string marker = Path.Combine(MirrorsDir, "test-session.skip");
        await Assert.That(File.Exists(marker)).IsTrue();

        // Explicit dirs: the env-driven overload also sweeps the real home dirs.
        MirrorFile.CollectGarbage([MirrorsDir]);
        await Assert.That(File.Exists(marker)).IsTrue(); // transcript alive: marker stays

        File.Delete(path);
        MirrorFile.CollectGarbage([MirrorsDir]);
        await Assert.That(File.Exists(marker)).IsFalse(); // transcript gone: marker reaped
    }

    [Test]
    public async Task RestoreIsIdempotent()
    {
        string path = BuildCompactableSession();
        Compactor.Run(path);
        await Assert.That(RunRestore(["test-session"], compactionOn: true)).IsEqualTo(0);
        string afterFirst = File.ReadAllText(path);
        await Assert.That(RunRestore(["test-session"], compactionOn: true)).IsEqualTo(0);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(afterFirst);
    }

    [Test]
    public async Task UnknownSessionFailsWithSearchedDirs()
    {
        await Assert.That(RunRestore(["deadbeef"], compactionOn: false)).IsEqualTo(1);
        await Assert.That(RunRestore([], compactionOn: false)).IsEqualTo(1);
    }
}
