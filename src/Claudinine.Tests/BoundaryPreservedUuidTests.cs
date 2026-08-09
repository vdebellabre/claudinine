namespace Claudinine.Tests;

/// <summary>
/// compactMetadata.preservedMessages.allUuids is a third reference class alongside
/// parentUuid and leafUuid: after a compact_boundary the app loads the summary PLUS
/// the records named there. Nothing in the parent chain points at them, so removing
/// one is invisible to dangling-parent validation.
///
/// Regression source (corpus d8aa7b17, 2026-08-09): StopHookSummaryStripRule removed
/// a bare stop_hook_summary — correct by that rule's own reckoning, zero signal — that
/// the boundary listed as preserved.
/// </summary>
public sealed class BoundaryPreservedUuidTests : IDisposable
{
    private readonly string _dir;

    public BoundaryPreservedUuidTests()
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

    /// <summary>
    /// A compact_boundary as the app writes it: parentUuid null (the physical chain is
    /// deliberately severed), logicalParentUuid pointing at the preserved tail.
    /// </summary>
    private static string BoundaryLine(string logicalParent, params string[] preservedUuids) =>
        new JsonObject
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
                    ["allUuids"] = new JsonArray([.. preservedUuids.Select(u => (JsonNode)u)]),
                },
            },
        }.ToJsonString();

    [Test]
    public async Task BareStopHookSummary_NamedByBoundary_IsKept()
    {
        var b = new TranscriptBuilder().UserPrompt("do the thing");
        b.StopHookSummary(); // bare: no errors, no context, no output → strip-eligible
        string preserved = b.LastUuid!;
        b.AssistantText("carry on");
        b.RawLine(BoundaryLine(preserved, preserved));
        b.AssistantText("after the boundary");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        var records = Load(path);
        await Assert.That(records.Any(r =>
            r["uuid"]?.GetValue<string>() == preserved)).IsTrue();
    }

    /// <summary>
    /// The guard must not disable the rule wholesale — an identical bare summary that
    /// no boundary references is still removed.
    /// </summary>
    [Test]
    public async Task BareStopHookSummary_NotNamedByBoundary_IsStillRemoved()
    {
        var b = new TranscriptBuilder().UserPrompt("do the thing");
        b.StopHookSummary();
        string unreferenced = b.LastUuid!;
        b.AssistantText("carry on");
        b.RawLine(BoundaryLine(unreferenced, Guid.NewGuid().ToString()));
        b.AssistantText("after the boundary");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        var records = Load(path);
        await Assert.That(records.Any(r =>
            r["uuid"]?.GetValue<string>() == unreferenced)).IsFalse();
    }

    /// <summary>
    /// The app names uuids that were never written (2 of 8 in d8aa7b17), so a
    /// preserved list referencing absent records must not abort the pass.
    /// </summary>
    [Test]
    public async Task PreservedUuidsAbsentFromFile_AreTolerated()
    {
        var b = new TranscriptBuilder().UserPrompt("do the thing");
        b.AssistantText("carry on");
        string logicalParent = b.LastUuid!;
        b.RawLine(BoundaryLine(logicalParent,
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString()));
        b.AssistantText("after the boundary");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        var records = Load(path);
        await Assert.That(records.Any(r =>
            r["subtype"]?.GetValue<string>() == "compact_boundary")).IsTrue();
    }
}
