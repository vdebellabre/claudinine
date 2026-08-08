using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claudinine.Mirror;
using Xunit;

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
        string output = "tool output " + new string('o', 400);
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

    private static int RunRestore(string[] args, bool compactionOn)
    {
        var sw = new StringWriter();
        TextWriter origOut = Console.Out, origErr = Console.Error;
        Console.SetOut(sw);
        Console.SetError(sw);
        try { return RestoreVerb.Run(args, compactionOn); }
        finally { Console.SetOut(origOut); Console.SetError(origErr); }
    }

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

    [Fact]
    public void UuidlessLinesMergeByMaxMultiplicityAcrossMirrors()
    {
        // Cross-context copies of the SAME session can hold the same uuid-less
        // line a different number of times; the restore must reproduce the
        // maximum — neither the sum (5) nor the first file's count (2).
        string a = WriteRawMirror("a.jsonl", QueueOp, QueueOp);
        string b = WriteRawMirror("b.jsonl", QueueOp, QueueOp, QueueOp);

        (List<RestoreVerb.Line> lines, bool forkMerged) = RestoreVerb.ReadMirrors([a, b]);

        Assert.False(forkMerged);
        Assert.Equal(3, lines.Count(l => l.Raw == QueueOp));
    }

    [Fact]
    public void MultiplicityMergeIsOrderIndependent()
    {
        string a = WriteRawMirror("a.jsonl", QueueOp, QueueOp);
        string b = WriteRawMirror("b.jsonl", QueueOp, QueueOp, QueueOp);

        // Larger file first: the smaller one must contribute nothing new.
        (List<RestoreVerb.Line> lines, _) = RestoreVerb.ReadMirrors([b, a]);

        Assert.Equal(3, lines.Count(l => l.Raw == QueueOp));
    }

    [Fact]
    public void UuidRecordsDedupAcrossMirrorsFirstFileWins()
    {
        string a = WriteRawMirror("a.jsonl",
            """{"type":"user","uuid":"u1","note":"from-a"}""");
        string b = WriteRawMirror("b.jsonl",
            """{"type":"user","uuid":"u1","note":"from-b"}""",
            """{"type":"user","uuid":"u2","note":"only-b"}""");

        (List<RestoreVerb.Line> lines, _) = RestoreVerb.ReadMirrors([a, b]);

        RestoreVerb.Line u1 = Assert.Single(lines, l => l.Uuid == "u1");
        Assert.Contains("from-a", u1.Raw);
        Assert.Single(lines, l => l.Uuid == "u2");
    }

    [Fact]
    public void ForkSeparatorSetsFlagButIsNotARecord()
    {
        string a = WriteRawMirror("a.jsonl",
            """{"type":"user","uuid":"u1"}""",
            """{"claudinine":{"v":"1","mergedFromFork":"parent-sid"}}""",
            """{"type":"user","uuid":"u0"}""");

        (List<RestoreVerb.Line> lines, bool forkMerged) = RestoreVerb.ReadMirrors([a]);

        Assert.True(forkMerged);
        Assert.Equal(2, lines.Count);
        Assert.DoesNotContain(lines, l => l.Raw.Contains("mergedFromFork"));
    }

    [Fact]
    public void RestoreRoundTripsToTheExactOriginal()
    {
        string path = BuildCompactableSession();
        string original = File.ReadAllText(path);

        Compactor.Run(path);
        Assert.NotEqual(original, File.ReadAllText(path)); // compaction did something

        int rc = RunRestore(["test-session"], compactionOn: true);
        Assert.Equal(0, rc);
        Assert.Equal(original, File.ReadAllText(path)); // byte-identical restore
    }

    [Fact]
    public void RestoreOffFreezesTheSession()
    {
        string path = BuildCompactableSession();
        Compactor.Run(path);

        Assert.Equal(0, RunRestore(["test-session"], compactionOn: false));
        Assert.True(File.Exists(Path.Combine(MirrorsDir, "test-session.skip")));
        string restored = File.ReadAllText(path);

        // Hooks now mirror but never compact: the restored file survives a pass.
        RunHook(path);
        Assert.Equal(restored, File.ReadAllText(path));
        RunHook(path, "SessionEnd");
        Assert.Equal(restored, File.ReadAllText(path));
    }

    [Fact]
    public void FrozenSessionStillGetsMirrored()
    {
        string path = BuildCompactableSession();
        Compactor.Run(path);
        Assert.Equal(0, RunRestore(["test-session"], compactionOn: false));

        // New turns land while frozen…
        var extra = new TranscriptBuilder().UserPrompt("post-freeze work");
        string extraPath = extra.WriteTo(_dir, "extra.jsonl");
        string newRecord = File.ReadAllLines(extraPath)[0]
            .Replace("00000000-0000-0000-0000-000000000001", "99999999-0000-0000-0000-000000000001");
        File.AppendAllText(path, newRecord + "\n");

        RunHook(path);

        // …and are mirrored despite compaction being off.
        string mirror = File.ReadAllText(Path.Combine(MirrorsDir, "test-session.jsonl"));
        Assert.Contains("99999999-0000-0000-0000-000000000001", mirror);
        Assert.Contains("post-freeze work", File.ReadAllText(path)); // transcript untouched
    }

    [Fact]
    public void RestoreOnUnfreezesAndCompactionResumes()
    {
        string path = BuildCompactableSession();
        Compactor.Run(path);
        Assert.Equal(0, RunRestore(["test-session"], compactionOn: false));

        Assert.Equal(0, RunRestore(["test-session"], compactionOn: true));
        Assert.False(File.Exists(Path.Combine(MirrorsDir, "test-session.skip")));

        string restored = File.ReadAllText(path);
        RunHook(path);
        Assert.NotEqual(restored, File.ReadAllText(path)); // compaction is back
    }

    [Fact]
    public void UnmirroredTailIsCapturedBeforeRestoring()
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

        Assert.Equal(0, RunRestore(["test-session"], compactionOn: true));
        string restored = File.ReadAllText(path);
        Assert.Contains("crash-tail prompt", restored);
        Assert.EndsWith("cccccccc-0000-0000-0000-000000000001",
            ((JsonObject)JsonNode.Parse(File.ReadAllLines(path)[^1])!)["uuid"]!.GetValue<string>());
    }

    [Fact]
    public void RestoreRefusedWhenMirrorMissesALiveRecord()
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

        Assert.Equal(1, RunRestore(["test-session"], compactionOn: true));
        Assert.Equal(compacted, File.ReadAllText(path)); // fail-closed: untouched
    }

    [Fact]
    public void ForkHealedMirrorIsReorderedParentFirst()
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

        Assert.Equal(0, RunRestore(["test-session"], compactionOn: true));

        string[] restored = File.ReadAllLines(path);
        Assert.Equal(3, restored.Length);
        Assert.Contains("pre-fork 1", restored[0]);
        Assert.Contains("pre-fork 2", restored[1]);
        Assert.Contains("fork continues", restored[2]); // live tail preserved as tail
    }

    [Fact]
    public void AppWriteQuirkOrderIsPreservedVerbatim()
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
        Assert.Equal(0, RunRestore(["test-session"], compactionOn: true));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void OrphanSkipMarkersAreCollected()
    {
        string path = BuildCompactableSession();
        Compactor.Run(path);
        Assert.Equal(0, RunRestore(["test-session"], compactionOn: false));
        string marker = Path.Combine(MirrorsDir, "test-session.skip");
        Assert.True(File.Exists(marker));

        // Explicit dirs: the env-driven overload also sweeps the real home dirs.
        MirrorFile.CollectGarbage([MirrorsDir]);
        Assert.True(File.Exists(marker)); // transcript alive: marker stays

        File.Delete(path);
        MirrorFile.CollectGarbage([MirrorsDir]);
        Assert.False(File.Exists(marker)); // transcript gone: marker reaped
    }

    [Fact]
    public void RestoreIsIdempotent()
    {
        string path = BuildCompactableSession();
        Compactor.Run(path);
        Assert.Equal(0, RunRestore(["test-session"], compactionOn: true));
        string afterFirst = File.ReadAllText(path);
        Assert.Equal(0, RunRestore(["test-session"], compactionOn: true));
        Assert.Equal(afterFirst, File.ReadAllText(path));
    }

    [Fact]
    public void UnknownSessionFailsWithSearchedDirs()
    {
        Assert.Equal(1, RunRestore(["deadbeef"], compactionOn: false));
        Assert.Equal(1, RunRestore([], compactionOn: false));
    }
}
