using System.Text.Json.Nodes;
using Claudinine.Mirror;
using Xunit;

namespace Claudinine.Tests;

public sealed class CompactorTests : IDisposable
{
    private readonly string _dir;
    private static readonly string LongOutput = new('x', 500);

    public CompactorTests()
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

    /// <summary>
    /// 8 identical reads, ONE PER TURN so chain-collapse (which collapses any
    /// settled multi-call turn) stays out of these dedup-focused tests. Supersession
    /// is whole-file, so the dedup behavior is identical: cutoff leaves the first
    /// two eligible, recency keeps the rest.
    /// </summary>
    private TranscriptBuilder EightIdenticalReads(out List<string> toolUseIds)
    {
        var b = new TranscriptBuilder();
        toolUseIds = [];
        for (int i = 0; i < 8; i++)
        {
            b.UserPrompt($"look at foo ({i})");
            b.BashRead("sed -n '1,100p' src/foo.cs", out string id, LongOutput);
            toolUseIds.Add(id);
        }
        b.AssistantText("done");
        return b;
    }

    private static JsonObject[] Load(string path) =>
        File.ReadAllLines(path).Where(l => l.Length > 0)
            .Select(l => (JsonObject)JsonNode.Parse(l)!).ToArray();

    private static string? ResultContent(JsonObject record, string toolUseId) =>
        (record["message"]?["content"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(b => b["tool_use_id"]?.GetValue<string>() == toolUseId)?
            ["content"]?.GetValue<string>();

    [Fact]
    public void SupersededReadsAreStubbedOutsideRecencyWindow()
    {
        string path = EightIdenticalReads(out var ids).WriteTo(_dir);
        string[] before = File.ReadAllLines(path);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        Assert.Equal(before.Length, records.Length);

        // Reads 0 and 1 are superseded and outside the recency window → stubbed.
        foreach (string id in ids[..2])
        {
            JsonObject stubbed = Assert.Single(records, r => ResultContent(r, id) is not null);
            string content = ResultContent(stubbed, id)!;
            Assert.StartsWith("[claudinine: file read superseded", content);
            Assert.Contains("src/foo.cs:1-100", content);
            Assert.NotNull(stubbed["claudinine"]);
            Assert.Equal(stubbed["uuid"]!.GetValue<string>(),
                stubbed["claudinine"]!["origUuid"]!.GetValue<string>());
        }
        // Reads 2..7 are within the recency window → untouched.
        foreach (string id in ids[2..])
        {
            JsonObject kept = Assert.Single(records, r => ResultContent(r, id) is not null);
            Assert.Equal(LongOutput, ResultContent(kept, id));
        }
        // Tail record untouched byte-for-byte.
        Assert.Equal(before[^1], File.ReadAllLines(path)[^1]);
    }

    [Fact]
    public void PassIsIdempotent()
    {
        string path = EightIdenticalReads(out _).WriteTo(_dir);
        Compactor.Run(path);
        string afterFirst = File.ReadAllText(path);
        Compactor.Run(path);
        Assert.Equal(afterFirst, File.ReadAllText(path));
    }

    [Fact]
    public void MirrorHoldsOriginalsBeforeCompaction()
    {
        string path = EightIdenticalReads(out _).WriteTo(_dir);
        string[] original = File.ReadAllLines(path);

        Compactor.Run(path);

        string mirrorPath = MirrorFile.PathFor(path);
        Assert.True(File.Exists(mirrorPath));
        string[] mirrorLines = File.ReadAllLines(mirrorPath);

        // Header first, then the ORIGINAL records verbatim — restore is a copy.
        JsonObject header = (JsonObject)JsonNode.Parse(mirrorLines[0])!;
        Assert.Equal(Path.GetFullPath(path), header["claudinine"]!["mirrorOf"]!.GetValue<string>());
        Assert.Equal(original, mirrorLines[1..]);
    }

    [Fact]
    public void MirrorGainsNothingNewOnRerunAndKeepsOriginals()
    {
        string path = EightIdenticalReads(out _).WriteTo(_dir);
        Compactor.Run(path);
        string mirrorAfterFirst = File.ReadAllText(MirrorFile.PathFor(path));
        Compactor.Run(path); // stubs now in transcript; marker records must be skipped
        Assert.Equal(mirrorAfterFirst, File.ReadAllText(MirrorFile.PathFor(path)));
    }

    [Fact]
    public void UnparseableLineAbortsWholePass()
    {
        string path = EightIdenticalReads(out _)
            .RawLine("not json at all")
            .WriteTo(_dir);
        string before = File.ReadAllText(path);
        Compactor.Run(path);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.False(File.Exists(MirrorFile.PathFor(path))); // mirror untouched too
    }

    [Fact]
    public void RewriteRefusesToReplaceTailRecord()
    {
        // Tail-uuid invariant: the app chains the next append off the file's final
        // record. No rule should ever target it (supersession is later-covers-
        // earlier), but the rewrite layer must refuse even if one does.
        string path = EightIdenticalReads(out _).WriteTo(_dir);
        string before = File.ReadAllText(path);

        var transcript = Claudinine.Transcript.TranscriptFile.TryLoad(path)!;
        transcript.Records[^1].Replacement =
            (JsonObject)transcript.Records[^1].Node.DeepClone();

        Assert.False(transcript.TryRewrite());
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void RewriteRefusesResultWhoseToolUseWasRemoved()
    {
        // Pair atomicity: a surviving result carrier whose tool_use record was
        // removed dangles its sourceToolAssistantUUID (and its tool_use_id has no
        // answering block on reload). No rule should do this; the rewrite layer
        // must refuse if one does.
        var b = new TranscriptBuilder().UserPrompt("look");
        b.ToolUse("sed -n '1,5p' a.txt", out string useId, out string useUuid);
        b.ToolResultFor(useId, useUuid, LongOutput);
        b.AssistantText("done");
        string path = b.WriteTo(_dir);
        string before = File.ReadAllText(path);

        var transcript = Claudinine.Transcript.TranscriptFile.TryLoad(path)!;
        transcript.Records.Single(r => r.Uuid == useUuid).Removed = true;

        Assert.False(transcript.TryRewrite());
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void ShortResultsAreNotWorthAStub()
    {
        var b = new TranscriptBuilder();
        for (int i = 0; i < 8; i++)
        {
            b.UserPrompt($"look ({i})"); // one read per turn: dedup-only scenario
            b.BashRead("sed -n '1,100p' src/foo.cs", out _, "short output");
        }
        b.AssistantText("done");
        string path = b.WriteTo(_dir);
        string before = File.ReadAllText(path);
        Compactor.Run(path);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void NarrowerLaterReadsDoNotSupersedeAWiderOne()
    {
        var b = new TranscriptBuilder().UserPrompt("look");
        b.BashRead("sed -n '1,100p' src/foo.cs", out string wideId, LongOutput);
        for (int i = 0; i < 7; i++)
        {
            b.UserPrompt($"narrow ({i})"); // one read per turn: dedup-only scenario
            b.BashRead("sed -n '50,60p' src/foo.cs", out _, LongOutput); // narrower: no cover
        }
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        // The wide read is not covered by the narrow ones and must survive
        // (the narrow read at position 1 IS superseded by its later twins —
        // that part is ordinary dedup).
        JsonObject[] records = Load(path);
        JsonObject wide = Assert.Single(records, r => ResultContent(r, wideId) is not null);
        Assert.Equal(LongOutput, ResultContent(wide, wideId));
    }

    [Fact]
    public void MirrorPreservesRepeatedIdenticalUuidlessRecords()
    {
        // Real transcripts repeat identical uuid-less lines (queue-operations);
        // the mirror must keep every copy or a restore loses records.
        string queueOp = """{"type":"queue-operation","operation":"dequeue","sessionId":"test-session"}""";
        string path = EightIdenticalReads(out _)
            .RawLine(queueOp)
            .RawLine(queueOp)
            .WriteTo(_dir);

        Compactor.Run(path);

        string[] mirror = File.ReadAllLines(MirrorFile.PathFor(path));
        Assert.Equal(File.ReadAllLines(path).Length, mirror.Length - 1); // header + all records
        Assert.Equal(2, mirror.Count(l => l == queueOp));
    }

    [Fact]
    public void MirrorDoesNotReAppendLeafRemappedUuidlessRecords()
    {
        // A last-prompt record whose leafUuid points into a collapsed span gets its
        // leaf remapped by the rewrite layer. On the NEXT pass that remapped variant
        // must not read as a new original (uuid-less identity ignores leafUuid) —
        // it would pollute the mirror with a rewritten copy.
        var b = new TranscriptBuilder().UserPrompt("investigate");
        string? removedUuid = null;
        for (int i = 0; i < 5; i++)
        {
            b.BashRead($"sed -n '1,5p' f{i}.txt", out _, LongOutput + i);
            if (i == 3) removedUuid = b.LastUuid;
        }
        b.RawLine($$"""{"type":"last-prompt","sessionId":"test-session","leafUuid":"{{removedUuid}}"}""");
        b.AssistantText("done");
        string path = b.WriteTo(_dir);
        int originalCount = File.ReadAllLines(path).Length;

        Compactor.Run(path); // collapse + leaf remap
        Compactor.Run(path); // second pass sees the remapped variant

        string[] mirror = File.ReadAllLines(MirrorFile.PathFor(path));
        Assert.Equal(originalCount, mirror.Length - 1); // header + originals, nothing more
    }

    [Fact]
    public void GarbageCollectionRemovesOrphanedMirrors()
    {
        string path = EightIdenticalReads(out _).WriteTo(_dir);
        Compactor.Run(path);
        string mirrorPath = MirrorFile.PathFor(path);
        Assert.True(File.Exists(mirrorPath));

        File.Delete(path);
        // Explicit dirs: the env-driven overload also sweeps the real home dirs.
        MirrorFile.CollectGarbage([Path.GetDirectoryName(mirrorPath)!]);
        Assert.False(File.Exists(mirrorPath));
    }

    [Fact]
    public void GarbageCollectionSweepsEveryKnownDirectory()
    {
        // Skip markers fan out to every dir holding the session's mirrors; an
        // uninstalled context's hooks never run again, so GC must sweep all known
        // dirs, not just this context's write dir.
        string otherDir = Path.Combine(_dir, "other-context", "mirrors");
        Directory.CreateDirectory(otherDir);
        string orphanMarker = Path.Combine(otherDir, "dead-session.skip");
        File.WriteAllText(orphanMarker,
            """{"claudinine":{"v":"1","skipCompactionOf":"C:\\gone\\dead-session.jsonl"}}""" + "\n");

        MirrorFile.CollectGarbage([otherDir]);

        Assert.False(File.Exists(orphanMarker));
    }

    // ---- platform line endings: dedicated preservation paths, previously untested ----

    [Fact]
    public void CrlfTranscriptStaysCrlfAndMirrorNormalizesToLf()
    {
        string path = EightIdenticalReads(out _).WriteTo(_dir, newline: "\r\n");

        Compactor.Run(path);

        string text = File.ReadAllText(path);
        Assert.Contains("[claudinine", text); // compaction actually happened
        string[] lines = text.Split('\n');
        Assert.Equal("", lines[^1]); // trailing newline preserved
        for (int i = 0; i < lines.Length - 1; i++)
            Assert.EndsWith("\r", lines[i]); // replaced AND untouched records stay CRLF
        Assert.DoesNotContain('\r', File.ReadAllText(MirrorFile.PathFor(path)));

        Compactor.Run(path); // idempotent on its own CRLF output
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public void MissingTrailingNewlineIsPreserved()
    {
        string path = EightIdenticalReads(out _).WriteTo(_dir, trailingNewline: false);

        Compactor.Run(path);

        string text = File.ReadAllText(path);
        Assert.Contains("[claudinine", text);
        Assert.False(text.EndsWith('\n'));

        Compactor.Run(path);
        Assert.Equal(text, File.ReadAllText(path));
    }

    // ---- load sentinels: fail closed on shapes we could corrupt ----

    [Fact]
    public void InvalidUtf8LeavesFileByteForByteUntouched()
    {
        // The default UTF8 decoder silently swaps invalid bytes for U+FFFD; a pass
        // over such a file would write the mangled text back. Load must refuse.
        string path = EightIdenticalReads(out _).WriteTo(_dir);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] = 0xFF; // invalid UTF-8 anywhere in the file
        File.WriteAllBytes(path, bytes);

        Compactor.Run(path);

        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void ByteOrderMarkLeavesFileUntouched()
    {
        // The app never writes a BOM; ReadAllText would strip it invisibly and the
        // rewrite would drop it from the file. Not our shape → do nothing.
        string path = EightIdenticalReads(out _).WriteTo(_dir);
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. File.ReadAllBytes(path)];
        File.WriteAllBytes(path, bytes);

        Compactor.Run(path);

        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void WrongTypedIdentityFieldLeavesFileUntouched()
    {
        // TryParse's Try- contract: a numeric uuid is an unfamiliar shape and must
        // abort the load, not crash the verb or half-parse the record.
        string path = EightIdenticalReads(out _)
            .RawLine("""{"type":"user","uuid":123,"sessionId":"test-session"}""")
            .AssistantText("tail")
            .WriteTo(_dir);
        byte[] before = File.ReadAllBytes(path);

        Compactor.Run(path);

        Assert.Equal(before, File.ReadAllBytes(path));
    }
}
