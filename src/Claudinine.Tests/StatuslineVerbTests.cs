namespace Claudinine.Tests;

/// <summary>
/// The statusline's core promise: it prices what a RELOAD would return, not the
/// standing mirror-vs-transcript gap. The gap survives a reload (the mirror keeps
/// fat originals forever), so before the load-stamp watermark the bar claimed
/// ~48k reclaimable seconds after resuming. These tests pin the watermark
/// semantics: only compaction the current buffer has not yet benefited from —
/// i.e. since the last SessionStart stamp — counts.
/// </summary>
public sealed class StatuslineVerbTests : IDisposable
{
    private readonly string _dir;

    public StatuslineVerbTests()
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
    /// Unique stem per test: statusline reads probe every known mirror dir
    /// (including the developer's real ones), so a shared "test-session" stem
    /// could collide with a leftover from outside the temp sandbox.
    /// </summary>
    private static string NewStem() => Guid.NewGuid().ToString("N");

    /// <summary>Several turns of fat tool output, enough for compaction to bite.</summary>
    private static TranscriptBuilder FatTurns(int turns = 8, int fatChars = 3000)
    {
        var b = new TranscriptBuilder();
        for (int i = 0; i < turns; i++)
        {
            b.UserPrompt($"look ({i})");
            b.BashRead($"sed -n '1,100p' src/foo{i}.cs", out _, new string('x', fatChars));
        }
        b.AssistantText("done");
        return b;
    }

    [Test]
    public async Task FreshSessionReportsCompactionAsReclaimable()
    {
        // Session started new: SessionStart stamped before any transcript
        // existed, then turns ran fat and were compacted per-prompt. The buffer
        // still holds the fat originals, so the whole shrinkage is reclaimable.
        string stem = NewStem();
        string path = Path.Combine(_dir, stem + ".jsonl");
        LoadStamp.Write(path);
        FatTurns().WriteTo(_dir, stem + ".jsonl");
        Compactor.Run(path);

        var m = StatuslineVerb.Measure(path);
        await Assert.That(m.HasValue).IsTrue();
        await Assert.That(m!.Value.RemovedBytes).IsGreaterThan(8 * 3000L / 2);
    }

    [Test]
    public async Task ReloadedSessionReportsNothing()
    {
        // The bug this file exists for: after /exit + resume the buffer was
        // re-seeded from the compacted file, so nothing is reclaimable — yet the
        // mirror still holds every fat original. The resume-time stamp saw the
        // records small; the standing gap must not be reported.
        string stem = NewStem();
        string path = FatTurns().WriteTo(_dir, stem + ".jsonl");
        Compactor.Run(path);
        LoadStamp.Write(path);

        await Assert.That(StatuslineVerb.Measure(path).HasValue).IsFalse();
    }

    [Test]
    public async Task NoStampStaysSilent()
    {
        // A session last loaded under a build without the watermark has no
        // stamp; silence beats the misleading standing-gap number.
        string stem = NewStem();
        string path = FatTurns().WriteTo(_dir, stem + ".jsonl");
        Compactor.Run(path);

        await Assert.That(StatuslineVerb.Measure(path).HasValue).IsFalse();
    }

    [Test]
    public async Task PostReloadCompactionCountsOnlyNewTurns()
    {
        // After a reload, new fat turns accrue and get compacted — those are
        // reclaimable; the pre-reload history is not. Enough turns are appended
        // that the early ones leave the recency window the rules keep intact.
        string stem = NewStem();
        string path = FatTurns().WriteTo(_dir, stem + ".jsonl");
        Compactor.Run(path);
        LoadStamp.Write(path);

        var b2 = new TranscriptBuilder();
        for (int i = 0; i < 500; i++)
            b2.NextUuid(); // disjoint uuid space from the pre-reload builder
        for (int i = 0; i < 8; i++)
        {
            b2.UserPrompt($"more work ({i})");
            b2.BashRead($"sed -n '1,100p' src/bar{i}.cs", out _, new string('y', 3000));
        }
        b2.AssistantText("done again");
        string appendix = b2.WriteTo(_dir, "appendix-" + stem + ".jsonl");
        File.AppendAllText(path, File.ReadAllText(appendix), new UTF8Encoding(false));
        Compactor.Run(path);

        // Independently reconstruct the post-reload shrinkage: records absent
        // from the stamp, priced mirror-original minus transcript-today.
        var stamp = LoadStamp.Read(path)!;
        var mirrorSizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach ((string uuid, long size) in LoadStamp.ScanRecordSizes(MirrorLocator.PathFor(path)))
            mirrorSizes[uuid] = size;
        long newOnly = 0;
        foreach ((string uuid, long size) in LoadStamp.ScanRecordSizes(path))
        {
            if (!stamp.ContainsKey(uuid)
                && mirrorSizes.TryGetValue(uuid, out long original) && original > size)
            {
                newOnly += original - size;
            }
        }

        var m = StatuslineVerb.Measure(path);
        await Assert.That(newOnly).IsGreaterThan(0L); // the appended turns did compact
        await Assert.That(m.HasValue).IsTrue();
        // Exact equality: any pre-reload byte leaking back in would break it.
        await Assert.That(m!.Value.RemovedBytes).IsEqualTo(newOnly);
    }

    [Test]
    public async Task CrashRepairCountsAsReclaimable()
    {
        // Crash left the file fat; the resume loaded it fat (stamped fat), then
        // the SessionStart repair pass compacted it. The buffer predates that
        // pass, so the shrinkage IS reclaimable even though stamp and mirror
        // agree on the original sizes.
        string stem = NewStem();
        string path = FatTurns().WriteTo(_dir, stem + ".jsonl");
        LoadStamp.Write(path);
        Compactor.Run(path);

        var m = StatuslineVerb.Measure(path);
        await Assert.That(m.HasValue).IsTrue();
        await Assert.That(m!.Value.RemovedBytes).IsGreaterThan(8 * 3000L / 2);
    }

    [Test]
    public async Task RenderReportsTokensForFreshSession()
    {
        string stem = NewStem();
        string path = Path.Combine(_dir, stem + ".jsonl");
        LoadStamp.Write(path);
        FatTurns().WriteTo(_dir, stem + ".jsonl");
        Compactor.Run(path);

        string? line = StatuslineVerb.Render(new StatuslineInput
        {
            TranscriptPath = path,
            ContextWindow = new ContextWindow { TotalInputTokens = 200_000 },
        });
        await Assert.That(line).IsNotNull();
        await Assert.That(line!).Contains("reclaimable");
        // Above the 50k gate the figure is reported in k-tokens, so the line must
        // carry a token count — not just a bare percentage.
        await Assert.That(line!).Contains("k tokens");
    }

    /// <summary>
    /// The gate is an absolute token count, so the SAME measured reclaim must
    /// show or hide purely on how many tokens it prices at. Both cases use one
    /// transcript and vary only the live window: tokens-per-byte is derived from
    /// it, so a small window puts the identical byte reclaim under the bar.
    /// Pins the threshold's units — a percentage gate would report both.
    /// </summary>
    [Test]
    public async Task RenderStaysSilentBelowTokenThreshold()
    {
        string stem = NewStem();
        string path = Path.Combine(_dir, stem + ".jsonl");
        LoadStamp.Write(path);
        FatTurns().WriteTo(_dir, stem + ".jsonl");
        Compactor.Run(path);

        // Sanity: the byte reclaim is a large FRACTION of this buffer, so the old
        // 5%-of-context gate would have printed a line here.
        var m = StatuslineVerb.Measure(path);
        await Assert.That(m.HasValue).IsTrue();
        double percent = (double)m!.Value.RemovedBytes / m.Value.UncompactedBytes * 100.0;
        await Assert.That(percent).IsGreaterThan(5.0);

        // ...but priced against a small window it is only a few thousand tokens.
        string? line = StatuslineVerb.Render(new StatuslineInput
        {
            TranscriptPath = path,
            ContextWindow = new ContextWindow { TotalInputTokens = 10_000 },
        });
        await Assert.That(line).IsNull();
    }

    /// <summary>
    /// No live window (before the first API response) means no token figure, and
    /// the token figure is the only gate — so there is nothing to judge and the
    /// bar says nothing, rather than falling back to a percent-only line held to
    /// a weaker bar than the user asked for.
    /// </summary>
    [Test]
    public async Task RenderStaysSilentWithoutLiveWindow()
    {
        string stem = NewStem();
        string path = Path.Combine(_dir, stem + ".jsonl");
        LoadStamp.Write(path);
        FatTurns().WriteTo(_dir, stem + ".jsonl");
        Compactor.Run(path);

        await Assert.That(StatuslineVerb.Measure(path).HasValue).IsTrue();
        string? line = StatuslineVerb.Render(new StatuslineInput { TranscriptPath = path });
        await Assert.That(line).IsNull();
    }

    [Test]
    public async Task RenderStaysSilentAfterReload()
    {
        string stem = NewStem();
        string path = FatTurns().WriteTo(_dir, stem + ".jsonl");
        Compactor.Run(path);
        LoadStamp.Write(path);

        string? line = StatuslineVerb.Render(new StatuslineInput
        {
            TranscriptPath = path,
            ContextWindow = new ContextWindow { TotalInputTokens = 200_000 },
        });
        await Assert.That(line).IsNull();
    }
}
