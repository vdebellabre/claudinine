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

    [Test]
    public async Task SupersededReadsAreStubbedOutsideRecencyWindow()
    {
        string path = EightIdenticalReads(out var ids).WriteTo(_dir);
        string[] before = File.ReadAllLines(path);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        await Assert.That(records.Length).IsEqualTo(before.Length);

        // Reads 0 and 1 are superseded and outside the recency window → stubbed.
        foreach (string id in ids[..2])
        {
            JsonObject stubbed = await Assert.That(records).HasSingleItem(r => ResultContent(r, id) is not null);
            string content = ResultContent(stubbed, id)!;
            await Assert.That(content).StartsWith("[claudinine: file read superseded");
            await Assert.That(content).Contains("src/foo.cs:1-100");
            await Assert.That(stubbed["claudinine"]).IsNotNull();
            await Assert.That(stubbed["claudinine"]!["origUuid"]!.GetValue<string>()).IsEqualTo(stubbed["uuid"]!.GetValue<string>());
        }
        // Reads 2..7 are within the recency window → untouched.
        foreach (string id in ids[2..])
        {
            JsonObject kept = await Assert.That(records).HasSingleItem(r => ResultContent(r, id) is not null);
            await Assert.That(ResultContent(kept, id)).IsEqualTo(LongOutput);
        }
        // Tail record untouched byte-for-byte.
        await Assert.That(File.ReadAllLines(path)[^1]).IsEqualTo(before[^1]);
    }

    [Test]
    public async Task PassIsIdempotent()
    {
        string path = EightIdenticalReads(out _).WriteTo(_dir);
        Compactor.Run(path);
        string afterFirst = File.ReadAllText(path);
        Compactor.Run(path);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(afterFirst);
    }

    [Test]
    public async Task MirrorHoldsOriginalsBeforeCompaction()
    {
        string path = EightIdenticalReads(out _).WriteTo(_dir);
        string[] original = File.ReadAllLines(path);

        Compactor.Run(path);

        string mirrorPath = MirrorLocator.PathFor(path);
        await Assert.That(File.Exists(mirrorPath)).IsTrue();
        string[] mirrorLines = File.ReadAllLines(mirrorPath);

        // Header first, then the ORIGINAL records verbatim — restore is a copy.
        JsonObject header = (JsonObject)JsonNode.Parse(mirrorLines[0])!;
        await Assert.That(header["claudinine"]!["mirrorOf"]!.GetValue<string>()).IsEqualTo(Path.GetFullPath(path));
        await Assert.That(mirrorLines[1..]).IsEquivalentTo(original);
    }

    [Test]
    public async Task MirrorGainsNothingNewOnRerunAndKeepsOriginals()
    {
        string path = EightIdenticalReads(out _).WriteTo(_dir);
        Compactor.Run(path);
        string mirrorAfterFirst = File.ReadAllText(MirrorLocator.PathFor(path));
        Compactor.Run(path); // stubs now in transcript; marker records must be skipped
        await Assert.That(File.ReadAllText(MirrorLocator.PathFor(path))).IsEqualTo(mirrorAfterFirst);
    }

    [Test]
    public async Task UnparseableLineAbortsWholePass()
    {
        string path = EightIdenticalReads(out _)
            .RawLine("not json at all")
            .WriteTo(_dir);
        string before = File.ReadAllText(path);
        Compactor.Run(path);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(before);
        await Assert.That(File.Exists(MirrorLocator.PathFor(path))).IsFalse(); // mirror untouched too
    }

    [Test]
    public async Task RewriteRefusesToReplaceTailRecord()
    {
        // Tail-uuid invariant: the app chains the next append off the file's final
        // record. No rule should ever target it (supersession is later-covers-
        // earlier), but the rewrite layer must refuse even if one does.
        string path = EightIdenticalReads(out _).WriteTo(_dir);
        string before = File.ReadAllText(path);

        var transcript = Claudinine.Transcript.TranscriptFile.TryLoad(path)!;
        transcript.Records[^1].Replacement =
            (JsonObject)transcript.Records[^1].Node.DeepClone();

        await Assert.That(transcript.TryRewrite()).IsFalse();
        await Assert.That(File.ReadAllText(path)).IsEqualTo(before);
    }

    [Test]
    public async Task RewriteRefusesWhenFileGrewBetweenLoadAndSwap()
    {
        // The app appends live during a turn; a record landing after our load
        // would be silently discarded by the swap — and mirror-first means it
        // was never mirrored either. The pre-swap length re-check must refuse.
        string path = EightIdenticalReads(out _).WriteTo(_dir);
        var transcript = Claudinine.Transcript.TranscriptFile.TryLoad(path)!;
        transcript.Records[1].Replacement =
            (JsonObject)transcript.Records[1].Node.DeepClone();

        File.AppendAllText(path, """{"type":"user","uuid":"late-append"}""" + "\n");
        string withLateAppend = File.ReadAllText(path);

        await Assert.That(transcript.TryRewrite()).IsFalse();
        await Assert.That(File.ReadAllText(path)).IsEqualTo(withLateAppend); // late record survives
    }

    [Test]
    public async Task RewriteRefusesResultWhoseToolUseWasRemoved()
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

        await Assert.That(transcript.TryRewrite()).IsFalse();
        await Assert.That(File.ReadAllText(path)).IsEqualTo(before);
    }

    [Test]
    public async Task ShortResultsAreNotWorthAStub()
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
        await Assert.That(File.ReadAllText(path)).IsEqualTo(before);
    }

    [Test]
    public async Task NarrowerLaterReadsDoNotSupersedeAWiderOne()
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
        JsonObject wide = await Assert.That(records).HasSingleItem(r => ResultContent(r, wideId) is not null);
        await Assert.That(ResultContent(wide, wideId)).IsEqualTo(LongOutput);
    }

    [Test]
    public async Task MirrorPreservesRepeatedIdenticalUuidlessRecords()
    {
        // Real transcripts repeat identical uuid-less lines (queue-operations);
        // the mirror must keep every copy or a restore loses records.
        string queueOp = """{"type":"queue-operation","operation":"dequeue","sessionId":"test-session"}""";
        string path = EightIdenticalReads(out _)
            .RawLine(queueOp)
            .RawLine(queueOp)
            .WriteTo(_dir);

        Compactor.Run(path);

        string[] mirror = File.ReadAllLines(MirrorLocator.PathFor(path));
        await Assert.That(mirror.Length - 1).IsEqualTo(File.ReadAllLines(path).Length); // header + all records
        await Assert.That(mirror.Count(l => l == queueOp)).IsEqualTo(2);
    }

    [Test]
    public async Task MirrorDoesNotReAppendLeafRemappedUuidlessRecords()
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

        string[] mirror = File.ReadAllLines(MirrorLocator.PathFor(path));
        await Assert.That(mirror.Length - 1).IsEqualTo(originalCount); // header + originals, nothing more
    }

    [Test]
    public async Task GarbageCollectionRemovesOrphanedMirrors()
    {
        string path = EightIdenticalReads(out _).WriteTo(_dir);
        Compactor.Run(path);
        string mirrorPath = MirrorLocator.PathFor(path);
        await Assert.That(File.Exists(mirrorPath)).IsTrue();

        File.Delete(path);
        // Explicit dirs: the env-driven overload also sweeps the real home dirs.
        MirrorFile.CollectGarbage([Path.GetDirectoryName(mirrorPath)!]);
        await Assert.That(File.Exists(mirrorPath)).IsFalse();
    }

    [Test]
    public async Task GarbageCollectionSweepsEveryKnownDirectory()
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

        await Assert.That(File.Exists(orphanMarker)).IsFalse();
    }

    // ---- platform line endings: dedicated preservation paths, previously untested ----

    [Test]
    public async Task CrlfTranscriptStaysCrlfAndMirrorNormalizesToLf()
    {
        string path = EightIdenticalReads(out _).WriteTo(_dir, newline: "\r\n");

        Compactor.Run(path);

        string text = File.ReadAllText(path);
        await Assert.That(text).Contains("[claudinine"); // compaction actually happened
        string[] lines = text.Split('\n');
        await Assert.That(lines[^1]).IsEqualTo(""); // trailing newline preserved
        for (int i = 0; i < lines.Length - 1; i++)
            await Assert.That(lines[i]).EndsWith("\r"); // replaced AND untouched records stay CRLF
        await Assert.That(File.ReadAllText(MirrorLocator.PathFor(path))).DoesNotContain('\r');

        Compactor.Run(path); // idempotent on its own CRLF output
        await Assert.That(File.ReadAllText(path)).IsEqualTo(text);
    }

    [Test]
    public async Task MissingTrailingNewlineIsPreserved()
    {
        string path = EightIdenticalReads(out _).WriteTo(_dir, trailingNewline: false);

        Compactor.Run(path);

        string text = File.ReadAllText(path);
        await Assert.That(text).Contains("[claudinine");
        await Assert.That(text.EndsWith('\n')).IsFalse();

        Compactor.Run(path);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(text);
    }

    // ---- load sentinels: fail closed on shapes we could corrupt ----

    [Test]
    public async Task InvalidUtf8LeavesFileByteForByteUntouched()
    {
        // The default UTF8 decoder silently swaps invalid bytes for U+FFFD; a pass
        // over such a file would write the mangled text back. Load must refuse.
        string path = EightIdenticalReads(out _).WriteTo(_dir);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] = 0xFF; // invalid UTF-8 anywhere in the file
        File.WriteAllBytes(path, bytes);

        Compactor.Run(path);

        await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(bytes);
    }

    [Test]
    public async Task ByteOrderMarkLeavesFileUntouched()
    {
        // The app never writes a BOM; ReadAllText would strip it invisibly and the
        // rewrite would drop it from the file. Not our shape → do nothing.
        string path = EightIdenticalReads(out _).WriteTo(_dir);
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. File.ReadAllBytes(path)];
        File.WriteAllBytes(path, bytes);

        Compactor.Run(path);

        await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(bytes);
    }

    [Test]
    public async Task WrongTypedIdentityFieldLeavesFileUntouched()
    {
        // TryParse's Try- contract: a numeric uuid is an unfamiliar shape and must
        // abort the load, not crash the verb or half-parse the record.
        string path = EightIdenticalReads(out _)
            .RawLine("""{"type":"user","uuid":123,"sessionId":"test-session"}""")
            .AssistantText("tail")
            .WriteTo(_dir);
        byte[] before = File.ReadAllBytes(path);

        Compactor.Run(path);

        await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(before);
    }
}
