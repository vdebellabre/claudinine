namespace Claudinine.Tests;

public sealed class ReadToolDedupTests : IDisposable
{
    private readonly string _dir;
    private static readonly string LongOutput = new('x', 500);

    public ReadToolDedupTests()
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

    private static List<ReadTarget> Extract(JsonObject input) =>
        new ReadToolDedupRule().ExtractTargets(new JsonObject
        {
            ["type"] = "tool_use", ["id"] = "t1", ["name"] = "Read", ["input"] = input,
        });

    [Test]
    public async Task PlainReadClaimsDefaultWindow()
    {
        var t = await Assert.That(Extract(new JsonObject { ["file_path"] = @"C:\src\foo.cs" })).HasSingleItem();
        await Assert.That(t).IsEqualTo(new ReadTarget(@"C:\src\foo.cs", 1, ReadToolDedupRule.DefaultReadLimit));
    }

    [Test]
    public async Task OffsetAndLimitMapToLineRange()
    {
        var t = await Assert.That(Extract(new JsonObject
        {
            ["file_path"] = "f.cs", ["offset"] = 100, ["limit"] = 50,
        })).HasSingleItem();
        await Assert.That(t).IsEqualTo(new ReadTarget("f.cs", 100, 149));
    }

    [Test]
    public async Task OffsetWithoutLimitClaimsDefaultWindowFromOffset()
    {
        var t = await Assert.That(Extract(new JsonObject { ["file_path"] = "f.cs", ["offset"] = 1500 })).HasSingleItem();
        await Assert.That(t).IsEqualTo(new ReadTarget("f.cs", 1500, 1500 + ReadToolDedupRule.DefaultReadLimit - 1));
    }

    [Test]
    public async Task UnknownInputFieldRefused() =>
        await Assert.That(Extract(new JsonObject { ["file_path"] = "f.pdf", ["pages"] = "1-5" })).IsEmpty();

    [Test]
    public async Task MissingPathRefused() => await Assert.That(Extract(new JsonObject { ["offset"] = 1 })).IsEmpty();

    [Test]
    public async Task ChainCollapseDigestCarrierNeverStomped()
    {
        // Regression (found 2026-08-07 on d8aa7b17): a chain-collapse digest
        // reuses the anchor Read's tool_use_id, so on the NEXT pass this rule saw
        // a long "result" for a superseded read and replaced the digest with its
        // stub — destroying every other [ref] line the digest carried. Anything
        // already claudinine-authored must be left alone.
        string digest = "[claudinine: this turn originally ran 14 separate tool calls."
            + new string('x', 500) + "]";
        var b = new TranscriptBuilder();
        b.UserPrompt("investigate");
        b.ToolRead(@"C:\src\foo.cs", out _, digest, offset: 10, limit: 100);
        for (int i = 0; i < 8; i++)
        {
            b.UserPrompt($"again ({i})");
            b.ToolRead(@"C:\src\foo.cs", out _, LongOutput, offset: 10, limit: 100);
        }
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        await Assert.That(File.ReadAllLines(path)).Contains(l => l.Contains("originally ran 14 separate tool calls"));
    }

    [Test]
    public async Task EndToEndSupersession()
    {
        // One read per turn: keeps chain-collapse out of this dedup-focused test.
        var b = new TranscriptBuilder();
        var ids = new List<string>();
        for (int i = 0; i < 8; i++)
        {
            b.UserPrompt($"look at foo ({i})");
            b.ToolRead(@"C:\src\foo.cs", out string id, LongOutput, offset: 10, limit: 100);
            ids.Add(id);
        }
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        string[] lines = File.ReadAllLines(path);
        int stubbed = lines.Count(l => l.Contains("[claudinine: file read superseded"));
        await Assert.That(stubbed).IsEqualTo(2); // 8 reads, recency keeps 6, first two superseded
        // Raw JSONL escapes backslashes, so match the serialized form.
        await Assert.That(string.Join("\n", lines)).Contains(@"C:\\src\\foo.cs:10-109");
    }

    [Test]
    public async Task TruncatedDefaultReadDoesNotSupersedeDeepOffsetRead()
    {
        var b = new TranscriptBuilder().UserPrompt("look");
        b.ToolRead("f.cs", out string deepId, LongOutput, offset: 2500, limit: 50);
        for (int i = 0; i < 7; i++)
        {
            b.UserPrompt($"again ({i})"); // one read per turn: no chain-collapse here
            b.ToolRead("f.cs", out _, LongOutput); // no limit: claims only 1..2000
        }
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        string[] lines = File.ReadAllLines(path);
        JsonObject deep = lines.Select(l => (JsonObject)JsonNode.Parse(l)!)
            .Single(r => (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>()
                .Any(x => x["tool_use_id"]?.GetValue<string>() == deepId) == true
                && r["type"]?.GetValue<string>() == "user");
        string content = (deep["message"]!["content"] as JsonArray)!.OfType<JsonObject>()
            .Single()["content"]!.GetValue<string>();
        await Assert.That(content).IsEqualTo(LongOutput); // deep read survives
    }
}
