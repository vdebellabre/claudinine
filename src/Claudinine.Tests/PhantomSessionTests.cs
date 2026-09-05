namespace Claudinine.Tests;

/// <summary>
/// Phantom sessions: Claude Desktop allocates sessions that fire SessionStart
/// and SessionEnd but never write a transcript — two per app launch with
/// cwd = home, and ~1 s-lived ones per project open (measured 2026-09-05) —
/// each leaving a marker-only `&lt;sid&gt;/claudinine/` (.load, .end, .lock)
/// behind. They are orphans under SessionDirGc's existing rule, but that sweep
/// only ever ran after the transcript-exists guard, and a project dir populated
/// solely by phantoms never sees a transcript-bearing session. So a SessionStart
/// with no transcript runs the sweep before exiting.
///
/// Why not delete at SessionEnd when no transcript exists (the obvious fix): a
/// real zero-prompt session has no transcript at SessionEnd either. CLI probe,
/// 2026-09-05: transcript MISSING at SessionStart and at UserPromptSubmit,
/// present at SessionEnd only once a prompt was recorded; two zero-prompt
/// sessions ended with it missing and left exactly the phantom signature. A
/// SessionEnd self-delete would destroy the `.end` marker a Cowork re-hydration
/// of that live session depends on. The 24 h grace is the disambiguator, so the
/// sweep is deferred, never immediate.
///
/// Safe to exercise SessionStart end-to-end here (unlike HookRunnerTests): the
/// no-transcript branch runs SessionDirGc alone, scoped to the transcript's
/// project dir — a temp dir — never the per-user legacy pools.
/// </summary>
public sealed class PhantomSessionTests : IDisposable
{
    private const string OwnSid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string TwinSid = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff";
    private const string OldSid = "11111111-2222-3333-4444-555555555555";

    private readonly string _dir;
    private readonly string _project;
    private readonly string _own;   // this session's transcript path — never created

    public PhantomSessionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "claudinine-tests", Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_dir, "proj");
        Directory.CreateDirectory(_project);
        _own = Path.Combine(_project, OwnSid + ".jsonl");
        Environment.SetEnvironmentVariable("CLAUDE_PLUGIN_DATA", Path.Combine(_dir, "plugin-data"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_PLUGIN_DATA", null);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static int Run(string eventName, string transcript, string sid) =>
        HookRunner.Run(new MemoryStream(Encoding.UTF8.GetBytes($$"""
            {"hook_event_name":"{{eventName}}","session_id":"{{sid}}","transcript_path":"{{transcript.Replace("\\", "\\\\")}}"}
            """)));

    /// <summary>
    /// A session dir exactly as a phantom leaves it: the real writers' .load
    /// and .end plus the zero-byte .lock, no transcript, nothing else.
    /// </summary>
    private string Phantom(string sid, bool old)
    {
        string transcript = Path.Combine(_project, sid + ".jsonl");
        LoadStamp.Write(transcript);
        EndMarker.Write(transcript);
        File.WriteAllText(Path.Combine(MirrorLocator.ClaudinineDirFor(transcript), sid + ".lock"), "");
        string dir = Path.Combine(_project, sid);
        if (old)
            AgeTree(dir);
        return dir;
    }

    private static void AgeTree(string dir)
    {
        DateTime old = DateTime.UtcNow - TimeSpan.FromDays(30);
        foreach (string entry in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(entry))
            {
                Directory.SetCreationTimeUtc(entry, old);
                Directory.SetLastWriteTimeUtc(entry, old);
            }
            else
            {
                File.SetCreationTimeUtc(entry, old);
                File.SetLastWriteTimeUtc(entry, old);
            }
        }
        Directory.SetCreationTimeUtc(dir, old);
        Directory.SetLastWriteTimeUtc(dir, old);
    }

    [Test]
    public async Task SessionStartWithoutTranscript_SweepsOldPhantomSibling()
    {
        string phantom = Phantom(OldSid, old: true);

        await Assert.That(Run("SessionStart", _own, OwnSid)).IsEqualTo(0);

        await Assert.That(Directory.Exists(phantom)).IsFalse();
    }

    // The two sessions of one app launch start milliseconds apart: each sees
    // the other's brand-new dir, and the grace window must hold it.
    [Test]
    public async Task SessionStartWithoutTranscript_KeepsFreshTwin()
    {
        string twin = Phantom(TwinSid, old: false);

        await Assert.That(Run("SessionStart", _own, OwnSid)).IsEqualTo(0);

        await Assert.That(Directory.Exists(twin)).IsTrue();
    }

    // The existing SessionStart contract for a brand-new session — stamp
    // "loaded nothing" — is untouched by the sweep, and the sweep never turns
    // on its own dir.
    [Test]
    public async Task SessionStartWithoutTranscript_StillStampsOwnDir()
    {
        Phantom(OldSid, old: true);

        await Assert.That(Run("SessionStart", _own, OwnSid)).IsEqualTo(0);

        string stamp = Path.Combine(MirrorLocator.ClaudinineDirFor(_own), OwnSid + ".load");
        await Assert.That(File.Exists(stamp)).IsTrue();
    }

    [Test]
    public async Task SessionStartWithoutTranscript_KeepsSiblingWhoseTranscriptExists()
    {
        string alive = Phantom(OldSid, old: true);
        File.WriteAllText(Path.Combine(_project, OldSid + ".jsonl"), "{}\n");

        await Assert.That(Run("SessionStart", _own, OwnSid)).IsEqualTo(0);

        await Assert.That(Directory.Exists(alive)).IsTrue();
    }

    // The decision this file exists to pin: a transcript-less SessionEnd is
    // NOT a phantom detector. It writes its marker and deletes nothing — not
    // its own dir, not a sibling's.
    [Test]
    public async Task SessionEndWithoutTranscript_DeletesNothing()
    {
        string phantom = Phantom(OldSid, old: true);

        await Assert.That(Run("SessionEnd", _own, OwnSid)).IsEqualTo(0);

        await Assert.That(Directory.Exists(phantom)).IsTrue();
        string marker = Path.Combine(MirrorLocator.ClaudinineDirFor(_own), OwnSid + ".end");
        await Assert.That(File.Exists(marker)).IsTrue();
    }

    // A real session's first prompt also arrives before its transcript exists;
    // the per-prompt path stays free of housekeeping.
    [Test]
    public async Task UserPromptSubmitWithoutTranscript_DeletesNothing()
    {
        string phantom = Phantom(OldSid, old: true);

        await Assert.That(Run("UserPromptSubmit", _own, OwnSid)).IsEqualTo(0);

        await Assert.That(Directory.Exists(phantom)).IsTrue();
    }

    // The sweep itself, on the exact on-disk shape: a session dir holding only
    // claudinine/ with the three sidecars, no subagents/ or tool-results/.
    [Test]
    public async Task SessionDirGc_ReapsOldMarkerOnlyDir()
    {
        string phantom = Phantom(OldSid, old: true);
        File.WriteAllText(_own, "{}\n");

        SessionDirGc.Run(_own, OwnSid);

        await Assert.That(Directory.Exists(phantom)).IsFalse();
    }
}
