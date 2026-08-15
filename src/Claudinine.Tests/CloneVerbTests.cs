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
    private async Task<string> CloneId()
    {
        string[] found = Directory.EnumerateFiles(_projectDir, "*.jsonl")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null && n != SourceId)
            .Select(n => n!)
            .ToArray();
        await Assert.That(found).HasSingleItem();
        return found[0];
    }

    private List<JsonObject> ReadRecords(string path) =>
        File.ReadLines(path, Encoding.UTF8)
            .Where(l => l.Length > 0)
            .Select(l => (JsonNode.Parse(l) as JsonObject)!)
            .ToList();

    [Test]
    public async Task RebindsSessionIdOnEveryRecord()
    {
        WriteTranscript(
            $"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}",
            $"{{\"type\":\"assistant\",\"uuid\":\"u2\",\"sessionId\":\"{SourceId}\"}}");

        await Assert.That(CloneVerb.Run([SourceId], _root)).IsEqualTo(0);

        string cloneId = await CloneId();
        foreach (JsonObject rec in ReadRecords(TranscriptPath(cloneId)))
            await Assert.That(rec["sessionId"]!.GetValue<string>()).IsEqualTo(cloneId);
    }

    [Test]
    public async Task PreservesUuidChainTopology()
    {
        // Mirror refs are addressed by uuid — rewriting them would orphan retrieval.
        WriteTranscript(
            $"{{\"type\":\"user\",\"uuid\":\"u1\",\"parentUuid\":null,\"sessionId\":\"{SourceId}\"}}",
            $"{{\"type\":\"assistant\",\"uuid\":\"u2\",\"parentUuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}");

        await Assert.That(CloneVerb.Run([SourceId], _root)).IsEqualTo(0);

        List<JsonObject> recs = ReadRecords(TranscriptPath((await CloneId())));
        await Assert.That(recs[0]["uuid"]!.GetValue<string>()).IsEqualTo("u1");
        await Assert.That(recs[1]["uuid"]!.GetValue<string>()).IsEqualTo("u2");
        await Assert.That(recs[1]["parentUuid"]!.GetValue<string>()).IsEqualTo("u1");
    }

    [Test]
    public async Task RewritesEmbeddedRetrievalCommands()
    {
        // A chain-collapse digest spells the session id literally in its get commands.
        string digest = $"[claudinine: ...\\n  claudinine get {SourceId} --ref REF --full\\n]";
        WriteTranscript(
            $"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"," +
            $"\"message\":{{\"content\":[{{\"type\":\"tool_result\",\"content\":\"{digest}\"}}]}}}}");

        await Assert.That(CloneVerb.Run([SourceId], _root)).IsEqualTo(0);

        string cloneId = await CloneId();
        string text = File.ReadAllText(TranscriptPath(cloneId));
        await Assert.That(text).Contains($"claudinine get {cloneId}");
        await Assert.That(text).DoesNotContain(SourceId);
    }

    [Test]
    public async Task PersistedOutputSidecarPathSurvivesClone()
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

        await Assert.That(CloneVerb.Run([SourceId], _root)).IsEqualTo(0);

        string cloneId = await CloneId();
        string cloned = ReadRecords(TranscriptPath(cloneId))[0]["message"]!["content"]![0]!["content"]!
            .GetValue<string>();
        await Assert.That(cloned).Contains(sidecar);                        // pointer intact
        await Assert.That(cloned).Contains($"claudinine get {cloneId}");    // retrieval retargeted
        await Assert.That(cloned).DoesNotContain($"claudinine get {SourceId}");
    }

    [Test]
    public async Task RepointsMirrorHeaderAtCloneTranscript()
    {
        // Load-bearing: CollectGarbage deletes mirrors whose mirrorOf target is gone,
        // so a verbatim header would have the clone's mirror collected when the source
        // transcript is archived.
        WriteTranscript($"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}");
        WriteMirror($"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}");

        await Assert.That(CloneVerb.Run([SourceId], _root)).IsEqualTo(0);

        string cloneId = await CloneId();
        // The clone's mirror is written to its own colocated dir, wherever the
        // source mirror lived.
        string mirrorPath = MirrorLocator.PathFor(TranscriptPath(cloneId));
        await Assert.That(File.Exists(mirrorPath)).IsTrue();
        JsonObject header = ReadRecords(mirrorPath)[0];
        await Assert.That(header["claudinine"]!["mirrorOf"]!.GetValue<string>()).IsEqualTo(Path.GetFullPath(TranscriptPath(cloneId)));
    }

    [Test]
    public async Task MirrorBodyKeepsUuidsSoRetrievalStillResolves()
    {
        WriteTranscript($"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}");
        WriteMirror(
            $"{{\"type\":\"user\",\"uuid\":\"abc12345\",\"sessionId\":\"{SourceId}\"," +
            "\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"original output\"}]}}");

        await Assert.That(CloneVerb.Run([SourceId], _root)).IsEqualTo(0);

        string cloneId = await CloneId();
        List<JsonObject> recs = ReadRecords(MirrorLocator.PathFor(TranscriptPath(cloneId)));
        await Assert.That(recs[1]["uuid"]!.GetValue<string>()).IsEqualTo("abc12345");
        await Assert.That(recs[1]["sessionId"]!.GetValue<string>()).IsEqualTo(cloneId);
        await Assert.That(recs[1].ToJsonString()).Contains("original output");
    }

    [Test]
    public async Task SuffixesExistingTitle()
    {
        WriteTranscript(
            $"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}",
            $"{{\"type\":\"custom-title\",\"customTitle\":\"My work\",\"sessionId\":\"{SourceId}\"}}");

        await Assert.That(CloneVerb.Run([SourceId], _root)).IsEqualTo(0);

        string text = File.ReadAllText(TranscriptPath((await CloneId())));
        await Assert.That(text).Contains("My work (compacted)");
    }

    [Test]
    public async Task AddsTitleWhenSourceHasNone()
    {
        // Without a title the app derives one from the first prompt, so both sessions
        // would read alike in the resume picker.
        WriteTranscript($"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}");

        await Assert.That(CloneVerb.Run([SourceId], _root)).IsEqualTo(0);

        string cloneId = await CloneId();
        List<JsonObject> recs = ReadRecords(TranscriptPath(cloneId));
        JsonObject title = await Assert.That(recs).HasSingleItem(r => r["type"]?.GetValue<string>() == "custom-title");
        await Assert.That(title["customTitle"]!.GetValue<string>()).Contains("(compacted)");
        await Assert.That(title["sessionId"]!.GetValue<string>()).IsEqualTo(cloneId);
    }

    [Test]
    public async Task DoesNotDoubleSuffixOnRecursiveClone()
    {
        WriteTranscript(
            $"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}",
            $"{{\"type\":\"custom-title\",\"customTitle\":\"My work (compacted)\",\"sessionId\":\"{SourceId}\"}}");

        await Assert.That(CloneVerb.Run([SourceId], _root)).IsEqualTo(0);

        string text = File.ReadAllText(TranscriptPath((await CloneId())));
        await Assert.That(text).Contains("My work (compacted)");
        await Assert.That(text).DoesNotContain("(compacted) (compacted)");
    }

    [Test]
    public async Task LeavesSourceUntouched()
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

        await Assert.That(CloneVerb.Run([SourceId], _root)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(TranscriptPath(SourceId))).IsEqualTo(before);
        await Assert.That(File.ReadAllText(Path.Combine(_mirrorDir, SourceId + ".jsonl"))).IsEqualTo(mirrorBefore);
    }

    [Test]
    public async Task ResolvesSessionByPrefix()
    {
        WriteTranscript($"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}");

        await Assert.That(CloneVerb.Run([SourceId[..8]], _root)).IsEqualTo(0);

        await Assert.That(File.Exists(TranscriptPath((await CloneId())))).IsTrue();
    }

    [Test]
    public async Task SucceedsWithoutMirror()
    {
        // A session compacted before the mirror existed, or one whose mirror aged out:
        // the clone is still worth making, just without retrieval.
        WriteTranscript($"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}");

        await Assert.That(CloneVerb.Run([SourceId], _root)).IsEqualTo(0);

        await Assert.That(File.Exists(TranscriptPath((await CloneId())))).IsTrue();
    }

    [Test]
    public async Task PreservesUnparseableLines()
    {
        WriteTranscript(
            $"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}",
            "{not json at all",
            $"{{\"type\":\"assistant\",\"uuid\":\"u2\",\"sessionId\":\"{SourceId}\"}}");

        await Assert.That(CloneVerb.Run([SourceId], _root)).IsEqualTo(0);

        string text = File.ReadAllText(TranscriptPath((await CloneId())));
        await Assert.That(text).Contains("{not json at all");
    }

    [Test]
    public async Task FailsOnUnknownSession()
    {
        await Assert.That(CloneVerb.Run(["11111111-2222-3333-4444-555555555555"], _root)).IsEqualTo(1);
    }

    [Test]
    public async Task FailsOnAmbiguousPrefix()
    {
        // The safety rule: a prefix matching two distinct sessions matches nothing —
        // cloning a guessed session would silently target the wrong history.
        WriteTranscript($"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}");
        string sibling = SourceId[..30] + "ffffff"; // same 8-char prefix, distinct id
        File.WriteAllText(TranscriptPath(sibling),
            $"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{sibling}\"}}\n",
            new UTF8Encoding(false));

        await Assert.That(CloneVerb.Run([SourceId[..8]], _root)).IsEqualTo(1);
    }

    [Test]
    public async Task FailsWithoutArguments()
    {
        await Assert.That(CloneVerb.Run([], _root)).IsEqualTo(1);
    }

    [Test]
    public async Task FailsOnUnknownArgument()
    {
        WriteTranscript($"{{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"{SourceId}\"}}");

        await Assert.That(CloneVerb.Run([SourceId, "--bogus"], _root)).IsEqualTo(1);
    }
}
