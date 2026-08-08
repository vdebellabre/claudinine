using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Claudinine.Tests;

/// <summary>
/// Clone correctness, driven against a temp profile. Home is passed to the verb
/// explicitly rather than via USERPROFILE: SpecialFolder.UserProfile reads the
/// registry on Windows and ignores the variable, so an env override would silently
/// leave the tests pointed at the developer's real sessions. CLAUDE_PLUGIN_DATA *is*
/// read from the environment, so the mirror dir is redirected that way.
/// </summary>
public sealed class CloneVerbTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectDir;
    private readonly string _mirrorDir;
    private readonly string? _oldProfile;
    private readonly string? _oldPluginData;
    private const string SourceId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    public CloneVerbTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "claudinine-tests", Guid.NewGuid().ToString("N"));
        _projectDir = Path.Combine(_root, ".claude", "projects", "C--proj");
        _mirrorDir = Path.Combine(_root, "plugin-data", "mirrors");
        Directory.CreateDirectory(_projectDir);
        Directory.CreateDirectory(_mirrorDir);

        _oldProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        _oldPluginData = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA");
        Environment.SetEnvironmentVariable("USERPROFILE", _root);
        Environment.SetEnvironmentVariable("HOME", _root);
        Environment.SetEnvironmentVariable("CLAUDE_PLUGIN_DATA", Path.Combine(_root, "plugin-data"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("USERPROFILE", _oldProfile);
        Environment.SetEnvironmentVariable("CLAUDE_PLUGIN_DATA", _oldPluginData);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string TranscriptPath(string id) => Path.Combine(_projectDir, id + ".jsonl");

    private void WriteTranscript(params string[] lines) =>
        File.WriteAllText(TranscriptPath(SourceId), string.Join("\n", lines) + "\n", new UTF8Encoding(false));

    private void WriteMirror(params string[] lines)
    {
        var header = new JsonObject
        {
            ["claudinine"] = new JsonObject
            {
                ["v"] = "1",
                ["mirrorOf"] = Path.GetFullPath(TranscriptPath(SourceId)),
            },
        };
        File.WriteAllText(
            Path.Combine(_mirrorDir, SourceId + ".jsonl"),
            string.Join("\n", new[] { header.ToJsonString() }.Concat(lines)) + "\n",
            new UTF8Encoding(false));
    }

    /// <summary>The clone's id: the one new transcript that is not the source.</summary>
    private string CloneId()
    {
        string[] found = Directory.EnumerateFiles(_projectDir, "*.jsonl")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null && n != SourceId)
            .Select(n => n!)
            .ToArray();
        Assert.Single(found);
        return found[0];
    }

    private List<JsonObject> ReadRecords(string path) =>
        File.ReadLines(path, Encoding.UTF8)
            .Where(l => l.Length > 0)
            .Select(l => (JsonNode.Parse(l) as JsonObject)!)
            .ToList();

    [Fact]
    public void RebindsSessionIdOnEveryRecord()
    {
        WriteTranscript(
            $"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}",
            $"{{\"type\":\"assistant\",\"uuid\":\"u2\",\"sessionId\":\"{SourceId}\"}}");

        Assert.Equal(0, CloneVerb.Run([SourceId], _root));

        string cloneId = CloneId();
        foreach (JsonObject rec in ReadRecords(TranscriptPath(cloneId)))
            Assert.Equal(cloneId, rec["sessionId"]!.GetValue<string>());
    }

    [Fact]
    public void PreservesUuidChainTopology()
    {
        // Mirror refs are addressed by uuid — rewriting them would orphan retrieval.
        WriteTranscript(
            $"{{\"type\":\"user\",\"uuid\":\"u1\",\"parentUuid\":null,\"sessionId\":\"{SourceId}\"}}",
            $"{{\"type\":\"assistant\",\"uuid\":\"u2\",\"parentUuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}");

        Assert.Equal(0, CloneVerb.Run([SourceId], _root));

        List<JsonObject> recs = ReadRecords(TranscriptPath(CloneId()));
        Assert.Equal("u1", recs[0]["uuid"]!.GetValue<string>());
        Assert.Equal("u2", recs[1]["uuid"]!.GetValue<string>());
        Assert.Equal("u1", recs[1]["parentUuid"]!.GetValue<string>());
    }

    [Fact]
    public void RewritesEmbeddedRetrievalCommands()
    {
        // A chain-collapse digest spells the session id literally in its get commands.
        string digest = $"[claudinine: ...\\n  claudinine get {SourceId} --ref REF --full\\n]";
        WriteTranscript(
            $"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"," +
            $"\"message\":{{\"content\":[{{\"type\":\"tool_result\",\"content\":\"{digest}\"}}]}}}}");

        Assert.Equal(0, CloneVerb.Run([SourceId], _root));

        string cloneId = CloneId();
        string text = File.ReadAllText(TranscriptPath(cloneId));
        Assert.Contains($"claudinine get {cloneId}", text);
        Assert.DoesNotContain(SourceId, text);
    }

    [Fact]
    public void PersistedOutputSidecarPathSurvivesClone()
    {
        // The app's <persisted-output> stubs embed an absolute sidecar path under
        // the SOURCE session's directory. The clone never copies that directory and
        // the path is the only pointer to the file — it must survive verbatim.
        // Only the `claudinine get <sid>` retrieval phrase gets retargeted.
        string sidecar = $@"C:\Users\u\.claude\projects\proj\{SourceId}\tool-results\x.txt";
        string content = "<persisted-output>\nFull output saved to: " + sidecar
            + $"\n</persisted-output>\nsee also: claudinine get {SourceId} --ref abc12345 --full";
        var rec = new JsonObject
        {
            ["type"] = "user",
            ["uuid"] = "u1",
            ["sessionId"] = SourceId,
            ["message"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = "t1",
                    ["content"] = content,
                }),
            },
        };
        WriteTranscript(rec.ToJsonString());

        Assert.Equal(0, CloneVerb.Run([SourceId], _root));

        string cloneId = CloneId();
        string cloned = ReadRecords(TranscriptPath(cloneId))[0]["message"]!["content"]![0]!["content"]!
            .GetValue<string>();
        Assert.Contains(sidecar, cloned);                        // pointer intact
        Assert.Contains($"claudinine get {cloneId}", cloned);    // retrieval retargeted
        Assert.DoesNotContain($"claudinine get {SourceId}", cloned);
    }

    [Fact]
    public void RepointsMirrorHeaderAtCloneTranscript()
    {
        // Load-bearing: CollectGarbage deletes mirrors whose mirrorOf target is gone,
        // so a verbatim header would have the clone's mirror collected when the source
        // transcript is archived.
        WriteTranscript($"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}");
        WriteMirror($"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}");

        Assert.Equal(0, CloneVerb.Run([SourceId], _root));

        string cloneId = CloneId();
        string mirrorPath = Path.Combine(_mirrorDir, cloneId + ".jsonl");
        Assert.True(File.Exists(mirrorPath));
        JsonObject header = ReadRecords(mirrorPath)[0];
        Assert.Equal(
            Path.GetFullPath(TranscriptPath(cloneId)),
            header["claudinine"]!["mirrorOf"]!.GetValue<string>());
    }

    [Fact]
    public void MirrorBodyKeepsUuidsSoRetrievalStillResolves()
    {
        WriteTranscript($"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}");
        WriteMirror(
            $"{{\"type\":\"user\",\"uuid\":\"abc12345\",\"sessionId\":\"{SourceId}\"," +
            "\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"original output\"}]}}");

        Assert.Equal(0, CloneVerb.Run([SourceId], _root));

        string cloneId = CloneId();
        List<JsonObject> recs = ReadRecords(Path.Combine(_mirrorDir, cloneId + ".jsonl"));
        Assert.Equal("abc12345", recs[1]["uuid"]!.GetValue<string>());
        Assert.Equal(cloneId, recs[1]["sessionId"]!.GetValue<string>());
        Assert.Contains("original output", recs[1].ToJsonString());
    }

    [Fact]
    public void SuffixesExistingTitle()
    {
        WriteTranscript(
            $"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}",
            $"{{\"type\":\"custom-title\",\"customTitle\":\"My work\",\"sessionId\":\"{SourceId}\"}}");

        Assert.Equal(0, CloneVerb.Run([SourceId], _root));

        string text = File.ReadAllText(TranscriptPath(CloneId()));
        Assert.Contains("My work (compacted)", text);
    }

    [Fact]
    public void AddsTitleWhenSourceHasNone()
    {
        // Without a title the app derives one from the first prompt, so both sessions
        // would read alike in the resume picker.
        WriteTranscript($"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}");

        Assert.Equal(0, CloneVerb.Run([SourceId], _root));

        string cloneId = CloneId();
        List<JsonObject> recs = ReadRecords(TranscriptPath(cloneId));
        JsonObject title = Assert.Single(
            recs, r => r["type"]?.GetValue<string>() == "custom-title");
        Assert.Contains("(compacted)", title["customTitle"]!.GetValue<string>());
        Assert.Equal(cloneId, title["sessionId"]!.GetValue<string>());
    }

    [Fact]
    public void DoesNotDoubleSuffixOnRecursiveClone()
    {
        WriteTranscript(
            $"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}",
            $"{{\"type\":\"custom-title\",\"customTitle\":\"My work (compacted)\",\"sessionId\":\"{SourceId}\"}}");

        Assert.Equal(0, CloneVerb.Run([SourceId], _root));

        string text = File.ReadAllText(TranscriptPath(CloneId()));
        Assert.Contains("My work (compacted)", text);
        Assert.DoesNotContain("(compacted) (compacted)", text);
    }

    [Fact]
    public void LeavesSourceUntouched()
    {
        string[] lines =
        [
            $"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}",
            $"{{\"type\":\"custom-title\",\"customTitle\":\"My work\",\"sessionId\":\"{SourceId}\"}}",
        ];
        WriteTranscript(lines);
        WriteMirror($"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}");
        string before = File.ReadAllText(TranscriptPath(SourceId));
        string mirrorBefore = File.ReadAllText(Path.Combine(_mirrorDir, SourceId + ".jsonl"));

        Assert.Equal(0, CloneVerb.Run([SourceId], _root));

        Assert.Equal(before, File.ReadAllText(TranscriptPath(SourceId)));
        Assert.Equal(mirrorBefore, File.ReadAllText(Path.Combine(_mirrorDir, SourceId + ".jsonl")));
    }

    [Fact]
    public void ResolvesSessionByPrefix()
    {
        WriteTranscript($"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}");

        Assert.Equal(0, CloneVerb.Run([SourceId[..8]], _root));

        Assert.True(File.Exists(TranscriptPath(CloneId())));
    }

    [Fact]
    public void SucceedsWithoutMirror()
    {
        // A session compacted before the mirror existed, or one whose mirror aged out:
        // the clone is still worth making, just without retrieval.
        WriteTranscript($"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}");

        Assert.Equal(0, CloneVerb.Run([SourceId], _root));

        Assert.True(File.Exists(TranscriptPath(CloneId())));
    }

    [Fact]
    public void PreservesUnparseableLines()
    {
        WriteTranscript(
            $"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}",
            "{not json at all",
            $"{{\"type\":\"assistant\",\"uuid\":\"u2\",\"sessionId\":\"{SourceId}\"}}");

        Assert.Equal(0, CloneVerb.Run([SourceId], _root));

        string text = File.ReadAllText(TranscriptPath(CloneId()));
        Assert.Contains("{not json at all", text);
    }

    [Fact]
    public void FailsOnUnknownSession()
    {
        Assert.Equal(1, CloneVerb.Run(["11111111-2222-3333-4444-555555555555"], _root));
    }

    [Fact]
    public void FailsOnAmbiguousPrefix()
    {
        // The safety rule: a prefix matching two distinct sessions matches nothing —
        // cloning a guessed session would silently target the wrong history.
        WriteTranscript($"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}");
        string sibling = SourceId[..30] + "ffffff"; // same 8-char prefix, distinct id
        File.WriteAllText(TranscriptPath(sibling),
            $"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{sibling}\"}}\n",
            new UTF8Encoding(false));

        Assert.Equal(1, CloneVerb.Run([SourceId[..8]], _root));
    }

    [Fact]
    public void FailsWithoutArguments()
    {
        Assert.Equal(1, CloneVerb.Run([], _root));
    }

    [Fact]
    public void FailsOnUnknownArgument()
    {
        WriteTranscript($"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}");

        Assert.Equal(1, CloneVerb.Run([SourceId, "--bogus"], _root));
    }
}
