using Claudinine.Transcript;

namespace Claudinine.Tests;

/// <summary>
/// The colocated mirror layout: canonical paths inside the session's sidecar
/// dir, one-time migration from the legacy flat pools, the missing-mirror
/// tripwire, and the structural sweep of a claudinine dir.
/// </summary>
public sealed class MirrorColocationTests : IDisposable
{
    private readonly string _dir;      // doubles as the fake HOME
    private readonly string _project;

    public MirrorColocationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "claudinine-tests", Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_dir, ".claude", "projects", "proj");
        Directory.CreateDirectory(_project);
        Environment.SetEnvironmentVariable("CLAUDE_PLUGIN_DATA", Path.Combine(_dir, "plugin-data"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_PLUGIN_DATA", null);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string LegacyPool => Path.Combine(_dir, "plugin-data", "mirrors");

    /// <summary>A session rich enough that chain-collapse fires (economics gate).</summary>
    private string BuildCompactableSession()
    {
        string output = "tool output " + new string('o', 2000);
        var b = new TranscriptBuilder().UserPrompt("do the thing");
        for (int i = 0; i < 3; i++)
            b.ToolCall("Bash", new JsonObject { ["command"] = $"echo step {i}" }, output + i);
        b.AssistantText("done");
        return b.WriteTo(_project);
    }

    [Test]
    public async Task SessionMirrorPathIsInsideTheSidecarDir()
    {
        string transcript = Path.Combine(_project, "test-session.jsonl");

        await Assert.That(MirrorLocator.PathFor(transcript)).IsEqualTo(
            Path.Combine(_project, "test-session", "claudinine", "test-session.jsonl"));
    }

    [Test]
    public async Task SubagentMirrorLandsInTheSessionsClaudinineDir()
    {
        string agent = Path.Combine(_project, "test-session", "subagents", "agent-x.jsonl");

        await Assert.That(MirrorLocator.PathFor(agent)).IsEqualTo(
            Path.Combine(_project, "test-session", "claudinine", "agent-x.jsonl"));
    }

    [Test]
    public async Task FindSessionMirrorsResolvesColocatedMirrorsAcrossProjects()
    {
        string transcript = BuildCompactableSession();
        Compactor.Run(transcript);
        string mirror = MirrorLocator.PathFor(transcript);
        await Assert.That(File.Exists(mirror)).IsTrue();

        var found = MirrorLocator.FindSessionMirrors("test-session", pluginData: null, home: _dir);

        await Assert.That(found).IsEquivalentTo([mirror]);
    }

    [Test]
    public async Task LegacyMirrorMigratesOnFirstTouchWithHeaderRepointed()
    {
        string transcript = BuildCompactableSession();
        Compactor.Run(transcript);
        string colocated = MirrorLocator.PathFor(transcript);

        // Rewind to the pre-colocation world: the mirror sits in the flat pool,
        // its header carrying a STALE absolute path (as after a machine move).
        Directory.CreateDirectory(LegacyPool);
        string legacy = Path.Combine(LegacyPool, "test-session.jsonl");
        string[] lines = File.ReadAllLines(colocated);
        var header = (JsonObject)JsonNode.Parse(lines[0])!;
        header["claudinine"]!["mirrorOf"] = @"C:\stale\old-home\test-session.jsonl";
        lines[0] = header.ToJsonString();
        File.WriteAllLines(legacy, lines);
        File.WriteAllText(legacy + ".seen", "claudinine-seen v1\nlen:0\n");
        Directory.Delete(Path.GetDirectoryName(colocated)!, recursive: true);

        Compactor.Run(transcript);

        await Assert.That(File.Exists(colocated)).IsTrue();
        await Assert.That(File.Exists(legacy)).IsFalse();          // moved, not copied
        await Assert.That(File.Exists(legacy + ".seen")).IsFalse();
        var migrated = (JsonObject)JsonNode.Parse(File.ReadLines(colocated).First())!;
        await Assert.That(migrated["claudinine"]!["mirrorOf"]!.GetValue<string>())
            .IsEqualTo(Path.GetFullPath(transcript));              // stale header healed
        // Body content survived the move: the originals are still retrievable.
        await Assert.That(File.ReadAllText(colocated)).Contains("echo step 0");
    }

    [Test]
    public async Task MissingMirrorTripwireStopsThePassWithoutCreatingAFreshMirror()
    {
        string transcript = BuildCompactableSession();
        Compactor.Run(transcript);
        string compacted = File.ReadAllText(transcript);
        await Assert.That(compacted).Contains(" get test-session"); // own stubs, either form

        // The loss: the claudinine dir is gone but the stubs remain.
        Directory.Delete(Path.Combine(_project, "test-session", "claudinine"), recursive: true);

        Compactor.Run(transcript);
        Compactor.MirrorOnly(transcript);

        await Assert.That(File.ReadAllText(transcript)).IsEqualTo(compacted); // untouched
        // No fresh mirror: the tripwire must stay armed and the loss visible.
        await Assert.That(File.Exists(MirrorLocator.PathFor(transcript))).IsFalse();
    }

    [Test]
    public async Task TripwireFiresOnPreLauncherStubsToo()
    {
        // Transcripts compacted by 0.1.x/0.2.x promise their mirror with the bare
        // `claudinine get <sid>` phrase (no launcher path). The tripwire must keep
        // matching that form forever, alongside the launcher form.
        var carrier = new JsonObject
        {
            ["type"] = "user",
            ["uuid"] = "99999999-0000-0000-0000-000000000099",
            ["parentUuid"] = null,
            ["sessionId"] = "test-session",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = "toolu_0001",
                    ["content"] = "[claudinine: this turn originally ran 2 separate tool calls. " +
                        "claudinine get test-session --ref REF --full]",
                }),
            },
            ["claudinine"] = new JsonObject { ["v"] = 1, ["rule"] = "chain-collapse" },
        }.ToJsonString();
        var b = new TranscriptBuilder().UserPrompt("hello");
        b.RawLine(carrier);
        b.AssistantText("done");
        string transcript = b.WriteTo(_project);
        string before = File.ReadAllText(transcript);

        // No mirror anywhere: the pass must stop dead — no rewrite, no fresh mirror.
        Compactor.Run(transcript);

        await Assert.That(File.ReadAllText(transcript)).IsEqualTo(before);
        await Assert.That(File.Exists(MirrorLocator.PathFor(transcript))).IsFalse();
    }

    [Test]
    public async Task TripwireDisarmsWhenTheMirrorComesBack()
    {
        string transcript = BuildCompactableSession();
        Compactor.Run(transcript);
        string mirror = MirrorLocator.PathFor(transcript);
        string savedMirror = File.ReadAllText(mirror);
        Directory.Delete(Path.Combine(_project, "test-session", "claudinine"), recursive: true);
        Compactor.Run(transcript); // tripwire pass, nothing happens

        // The user restores the claudinine dir (snapshot recovery, backup).
        Directory.CreateDirectory(Path.GetDirectoryName(mirror)!);
        File.WriteAllText(mirror, savedMirror, new UTF8Encoding(false));

        // New fat turns get mirrored again — the pass is alive.
        var extra = new TranscriptBuilder().UserPrompt("post-recovery work");
        string extraPath = extra.WriteTo(_dir, "extra.jsonl");
        string newRecord = File.ReadAllLines(extraPath)[0]
            .Replace("00000000-0000-0000-0000-000000000001", "99999999-0000-0000-0000-000000000001");
        File.AppendAllText(transcript, newRecord + "\n");
        Compactor.Run(transcript);

        await Assert.That(File.ReadAllText(mirror))
            .Contains("99999999-0000-0000-0000-000000000001");
    }

    [Test]
    public async Task ColocatedSweepReapsOrphanedSubagentMirrorsOnly()
    {
        string transcript = BuildCompactableSession();
        string sessionDir = Path.Combine(_project, "test-session");
        string claudinine = Path.Combine(sessionDir, "claudinine");
        Compactor.Run(transcript);

        // One live agent, one whose transcript is gone.
        Directory.CreateDirectory(Path.Combine(sessionDir, "subagents"));
        File.WriteAllText(Path.Combine(sessionDir, "subagents", "agent-live.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(claudinine, "agent-live.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(claudinine, "agent-dead.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(claudinine, "agent-dead.jsonl.seen"), "claudinine-seen v1\nlen:0\n");

        MirrorFile.CollectGarbageColocated(claudinine);

        await Assert.That(File.Exists(Path.Combine(claudinine, "agent-live.jsonl"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(claudinine, "test-session.jsonl"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(claudinine, "agent-dead.jsonl"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(claudinine, "agent-dead.jsonl.seen"))).IsFalse();
    }

    [Test]
    public async Task ColocatedSweepIgnoresHeaderPathsEntirely()
    {
        // Cross-device sync scenario: the whole tree moved, every mirrorOf header
        // is stale — but the transcript sits right there next to the mirror. The
        // structural sweep must keep it where the legacy header-based sweep would
        // have deleted it.
        string transcript = BuildCompactableSession();
        Compactor.Run(transcript);
        string mirror = MirrorLocator.PathFor(transcript);
        string[] lines = File.ReadAllLines(mirror);
        var header = (JsonObject)JsonNode.Parse(lines[0])!;
        header["claudinine"]!["mirrorOf"] = @"C:\stale\old-home\test-session.jsonl";
        lines[0] = header.ToJsonString();
        File.WriteAllLines(mirror, lines);

        MirrorFile.CollectGarbageColocated(Path.GetDirectoryName(mirror)!);

        await Assert.That(File.Exists(mirror)).IsTrue();
    }

    [Test]
    public async Task ForkAdoptsAColocatedParentMirror()
    {
        // Parent session, colocated mirror.
        var pb = new TranscriptBuilder().UserPrompt("parent work");
        string parentPath = pb.WriteTo(_project, "parent-session.jsonl");
        Compactor.Run(parentPath);
        await Assert.That(File.Exists(MirrorLocator.PathFor(parentPath))).IsTrue();

        // Sibling fork in the same project dir, disjoint uuid space (identical
        // uuids would dedup the merge away).
        var fb = new TranscriptBuilder();
        for (int i = 0; i < 50; i++)
            fb.NextUuid();
        fb.UserPrompt("fork continues");
        string forkPath = fb.WriteTo(_project, "fork-session.jsonl");
        var fork = TranscriptFile.TryLoad(forkPath)!;
        Compactor.Run(forkPath); // gives the fork its own colocated mirror

        bool adopted = MirrorFile.TryAdoptForkParent("parent-session", fork);

        await Assert.That(adopted).IsTrue();
        string forkMirror = File.ReadAllText(MirrorLocator.PathFor(forkPath));
        await Assert.That(forkMirror).Contains("mergedFromFork");
        await Assert.That(forkMirror).Contains("parent work");
    }
}
