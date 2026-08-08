namespace Claudinine.Tests;

/// <summary>
/// task-reminder-keep-last: every task_reminder attachment carries a FULL
/// snapshot of the session's task list, so any reminder with a later main-chain
/// reminder in file order is strictly stale and removed. Runs end-to-end through
/// Compactor.Run so removal rechaining and rewrite validation are exercised.
/// </summary>
public sealed class TaskReminderKeepLastTests : IDisposable
{
    private readonly string _dir;

    public TaskReminderKeepLastTests()
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

    private static JsonObject[] Reminders(JsonObject[] records) =>
        records.Where(r => r["type"]?.GetValue<string>() == "attachment"
            && r["attachment"]?["type"]?.GetValue<string>() == "task_reminder").ToArray();

    private static string? FirstSubject(JsonObject reminder) =>
        reminder["attachment"]?["content"] is JsonArray { Count: > 0 } items
            ? items[0]?["subject"]?.GetValue<string>()
            : null;

    [Test]
    public async Task KeepsExactlyTheLast_EmptyNudgesAndSnapshotsAlike()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .TaskReminder() // empty nudge
            .AssistantText("turn one")
            .TaskReminder("plan v1")
            .AssistantText("turn two")
            .TaskReminder("plan v2")
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        JsonObject survivor = await Assert.That(Reminders(Load(path))).HasSingleItem();
        await Assert.That(FirstSubject(survivor)).IsEqualTo("plan v2");
    }

    [Test]
    public async Task RemovedReminder_ChildRechainedToSurvivingAncestor()
    {
        var b = new TranscriptBuilder()
            .UserPrompt("hello")
            .AssistantText("turn one");
        string assistantUuid = b.LastUuid!;
        b.TaskReminder("plan v1")
            .UserPrompt("second prompt")
            .TaskReminder("plan v2")
            .AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        // The second prompt was parented to the removed v1 reminder: re-parents
        // to the surviving assistant record before it.
        JsonObject secondPrompt = records.Single(r =>
            r["message"]?["content"] is JsonValue v
            && v.TryGetValue<string>(out string? s) && s == "second prompt");
        await Assert.That(secondPrompt["parentUuid"]!.GetValue<string>()).IsEqualTo(assistantUuid);
    }

    [Test]
    public async Task SingleReminderUntouched()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .TaskReminder("plan v1")
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        await Assert.That(Reminders(Load(path))).HasSingleItem();
    }

    [Test]
    public async Task LastReminderAtTail_EarlierRemovedTailIntact()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .TaskReminder("plan v1")
            .AssistantText("done")
            .TaskReminder("plan v2")
            .WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        JsonObject survivor = await Assert.That(Reminders(records)).HasSingleItem();
        await Assert.That(FirstSubject(survivor)).IsEqualTo("plan v2");
        await Assert.That(survivor).IsSameReferenceAs(records[^1]);
    }

    [Test]
    public async Task SidechainReminders_NeitherRemovedNorSuperseding()
    {
        // A subagent's reminder is a separate conversation: it must not be
        // removed by a later main-chain reminder, and it must not supersede an
        // earlier main-chain one (fail-closed — no corpus occurrence exists).
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .TaskReminder("main v1")
            .TaskReminder("side v1", sidechain: true)
            .AssistantText("mid")
            .TaskReminder("main v2")
            .TaskReminder("side v2", sidechain: true)
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] reminders = Reminders(Load(path));
        string[] subjects = [.. reminders.Select(r => FirstSubject(r) ?? "(empty)")];
        await Assert.That(subjects).IsEquivalentTo(["side v1", "main v2", "side v2"]);
    }

    [Test]
    public async Task IdempotentSecondPass()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .TaskReminder()
            .TaskReminder("plan v1")
            .TaskReminder("plan v2")
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);
        byte[] afterFirst = File.ReadAllBytes(path);
        Compactor.Run(path);
        byte[] afterSecond = File.ReadAllBytes(path);

        await Assert.That(afterSecond).IsEquivalentTo(afterFirst);
    }
}
