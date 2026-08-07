using System.Text.Json.Nodes;
using Claudinine.Rules;
using Xunit;

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

    [Fact]
    public void PlainReadClaimsDefaultWindow()
    {
        var t = Assert.Single(Extract(new JsonObject { ["file_path"] = @"C:\src\foo.cs" }));
        Assert.Equal(new ReadTarget(@"C:\src\foo.cs", 1, ReadToolDedupRule.DefaultReadLimit), t);
    }

    [Fact]
    public void OffsetAndLimitMapToLineRange()
    {
        var t = Assert.Single(Extract(new JsonObject
        {
            ["file_path"] = "f.cs", ["offset"] = 100, ["limit"] = 50,
        }));
        Assert.Equal(new ReadTarget("f.cs", 100, 149), t);
    }

    [Fact]
    public void OffsetWithoutLimitClaimsDefaultWindowFromOffset()
    {
        var t = Assert.Single(Extract(new JsonObject { ["file_path"] = "f.cs", ["offset"] = 1500 }));
        Assert.Equal(new ReadTarget("f.cs", 1500, 1500 + ReadToolDedupRule.DefaultReadLimit - 1), t);
    }

    [Fact]
    public void UnknownInputFieldRefused() =>
        Assert.Empty(Extract(new JsonObject { ["file_path"] = "f.pdf", ["pages"] = "1-5" }));

    [Fact]
    public void MissingPathRefused() => Assert.Empty(Extract(new JsonObject { ["offset"] = 1 }));

    [Fact]
    public void EndToEndSupersession()
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
        Assert.Equal(2, stubbed); // 8 reads, recency keeps 6, first two superseded
        // Raw JSONL escapes backslashes, so match the serialized form.
        Assert.Contains(@"C:\\src\\foo.cs:10-109", string.Join("\n", lines));
    }

    [Fact]
    public void TruncatedDefaultReadDoesNotSupersedeDeepOffsetRead()
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
        Assert.Equal(LongOutput, content); // deep read survives
    }
}
