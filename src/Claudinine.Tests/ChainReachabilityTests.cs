namespace Claudinine.Tests;

/// <summary>
/// Reachability is a global invariant no per-record check can express: the app
/// reconstructs a conversation by walking parentUuid BACKWARDS from the last
/// record, so a record with a perfectly valid parent link can still be dropped
/// at load if the walk never reaches it.
///
/// Measured on 2.1.227 with throwaway 6-record fixtures resumed via --resume:
///   all parentUuid null   → 1 of 6 records loaded, warnIfTranscriptUnchained logged
///   one link broken at #4 → 3 of 6 records loaded, NOTHING logged
/// Exit code 0 and empty stderr in both cases. The app's own warning returns on
/// the first record carrying a parentUuid, so it fires only for total chain loss;
/// the partial break — the realistic rewrite bug — produces no signal whatsoever.
///
/// Hence the check lives on our write path. It compares reachable-before against
/// reachable-after (a delta, not an absolute): compact_boundary records carry
/// parentUuid null by design and original files legally contain unresolvable
/// refs, so an absolute assertion would refuse every compacted transcript.
/// </summary>
public sealed class ChainReachabilityTests : IDisposable
{
    private readonly string _dir;

    public ChainReachabilityTests()
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

    /// <summary>Walk parentUuid from the tail, exactly as the app's loader does.</summary>
    private static HashSet<string> ReachableFromTail(JsonObject[] records)
    {
        var parentOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (r["uuid"]?.GetValue<string>() is string u)
                parentOf.TryAdd(u, r["parentUuid"]?.GetValue<string>());
        }
        string? cursor = records[^1]["uuid"]?.GetValue<string>();
        var reached = new HashSet<string>(StringComparer.Ordinal);
        while (cursor is not null && reached.Add(cursor))
        {
            if (!parentOf.TryGetValue(cursor, out string? parent))
                break;
            cursor = parent;
        }
        return reached;
    }

    /// <summary>Uuid-bearing records the loader would drop at load time.</summary>
    private static string[] StrandedRecords(JsonObject[] records)
    {
        var reachable = ReachableFromTail(records);
        return [.. records
            .Select(r => r["uuid"]?.GetValue<string>())
            .Where(u => u is not null && !reachable.Contains(u))
            .Select(u => u!)];
    }

    /// <summary>
    /// The property that matters: after any real pass, every surviving record is
    /// still reachable from the tail. A dedup-heavy transcript exercises the
    /// removal/rechain path that could strand records.
    /// </summary>
    [Test]
    public async Task AfterCompaction_EverySurvivingRecordStaysReachable()
    {
        var b = new TranscriptBuilder().UserPrompt("read it twice");
        b.ToolRead("/repo/big.cs", out _, new string('x', 5000));
        b.AssistantText("first look");
        b.ToolRead("/repo/big.cs", out _, new string('x', 5000));
        b.AssistantText("second look");
        b.ToolRead("/repo/other.cs", out _, new string('y', 5000));
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        await Assert.That(StrandedRecords(Load(path))).IsEmpty();
    }

    /// <summary>
    /// A transcript that arrives already broken must not be made worse, and must
    /// not abort the pass either — inherited damage is not ours to fix, but we may
    /// not lose further ground. Reachable-after is compared against the inherited
    /// reachable-before.
    /// </summary>
    [Test]
    public async Task InheritedBreak_IsToleratedAndNotWorsened()
    {
        var b = new TranscriptBuilder().UserPrompt("start");
        b.ToolRead("/repo/dup.cs", out _, new string('x', 5000));
        b.AssistantText("mid");
        b.ToolRead("/repo/dup.cs", out _, new string('x', 5000));
        // A record whose parent never existed: the break the app would stop at.
        b.RawLine(new JsonObject
        {
            ["parentUuid"] = "deadbeef-0000-4000-8000-000000000099",
            ["isSidechain"] = false,
            ["type"] = "assistant",
            ["uuid"] = Guid.NewGuid().ToString(),
            ["sessionId"] = "test-session",
            ["message"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray([new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "after the break",
                }]),
            },
        }.ToJsonString());
        b.AssistantText("tail");
        string path = b.WriteTo(_dir);

        var reachableBefore = ReachableFromTail(Load(path));

        Compactor.Run(path);

        // No record that was reachable and still survives may have been stranded.
        var after = Load(path);
        var reachableAfter = ReachableFromTail(after);
        var newlyStranded = after
            .Select(r => r["uuid"]?.GetValue<string>())
            .Where(u => u is not null && reachableBefore.Contains(u) && !reachableAfter.Contains(u))
            .ToArray();
        await Assert.That(newlyStranded).IsEmpty();
    }

    /// <summary>
    /// The case the pre-existing dangling-parent check structurally cannot see, and
    /// the reason this guard exists. In a fork, two records share a parent while
    /// only one lies on the tail walk. A record on the off-path branch has a valid,
    /// surviving parentUuid — nothing dangles — yet the loader never reaches it.
    ///
    /// Verified directly against the walk semantics: with a &lt;- c &lt;- d, rechaining
    /// d past c to a leaves c reachable-before but unreachable-after, and the guard
    /// refuses on c. Inherited off-path branches (stranded both before and after)
    /// are left alone, which is what keeps fork copies and grafts loadable.
    /// </summary>
    [Test]
    public async Task OffPathBranch_StrandedBefore_DoesNotBlockThePass()
    {
        var b = new TranscriptBuilder().UserPrompt("start");
        string root = b.LastUuid!;
        b.ToolRead("/repo/dup.cs", out _, new string('x', 5000));
        b.AssistantText("on-path");
        // A sibling hanging off the root: valid parent, never on the tail walk.
        b.RawLine(new JsonObject
        {
            ["parentUuid"] = root,
            ["isSidechain"] = false,
            ["type"] = "assistant",
            ["uuid"] = Guid.NewGuid().ToString(),
            ["sessionId"] = "test-session",
            ["message"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray([new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "abandoned branch",
                }]),
            },
        }.ToJsonString());
        b.ToolRead("/repo/dup.cs", out _, new string('x', 5000));
        b.AssistantText("tail");
        string path = b.WriteTo(_dir);

        var reachableBefore = ReachableFromTail(Load(path));
        long lengthBefore = new FileInfo(path).Length;

        Compactor.Run(path);

        // The pass proceeded despite the pre-existing off-path branch...
        await Assert.That(new FileInfo(path).Length).IsLessThan(lengthBefore);
        // ...and stranded nothing that had been reachable.
        var after = Load(path);
        var reachableAfter = ReachableFromTail(after);
        var newlyStranded = after
            .Select(r => r["uuid"]?.GetValue<string>())
            .Where(u => u is not null && reachableBefore.Contains(u) && !reachableAfter.Contains(u))
            .ToArray();
        await Assert.That(newlyStranded).IsEmpty();
    }

    /// <summary>
    /// A compact_boundary severs the physical chain on purpose (parentUuid null,
    /// ancestry in logicalParentUuid). The check must not read that as damage —
    /// otherwise it refuses every compacted transcript, which is most of them.
    /// </summary>
    [Test]
    public async Task CompactBoundary_DoesNotCountAsUnreachable()
    {
        var b = new TranscriptBuilder().UserPrompt("before the boundary");
        b.ToolRead("/repo/dup.cs", out _, new string('x', 5000));
        b.AssistantText("carry on");
        string logicalParent = b.LastUuid!;
        b.RawLine(new JsonObject
        {
            ["parentUuid"] = null,
            ["logicalParentUuid"] = logicalParent,
            ["isSidechain"] = false,
            ["type"] = "system",
            ["subtype"] = "compact_boundary",
            ["uuid"] = Guid.NewGuid().ToString(),
            ["sessionId"] = "test-session",
            ["compactMetadata"] = new JsonObject
            {
                ["trigger"] = "auto",
                ["preTokens"] = 999320,
                ["postTokens"] = 18044,
                ["preservedMessages"] = new JsonObject
                {
                    ["allUuids"] = new JsonArray([(JsonNode)logicalParent]),
                },
            },
        }.ToJsonString());
        b.ToolRead("/repo/dup.cs", out _, new string('x', 5000));
        b.AssistantText("after the boundary");
        string path = b.WriteTo(_dir);

        long lengthBefore = new FileInfo(path).Length;

        Compactor.Run(path);

        // The pass must have gone through (duplicate reads collapsed), not been
        // refused by the reachability guard on account of the boundary.
        await Assert.That(new FileInfo(path).Length).IsLessThan(lengthBefore);
        await Assert.That(Load(path).Any(r =>
            r["subtype"]?.GetValue<string>() == "compact_boundary")).IsTrue();
    }
}
