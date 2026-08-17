namespace Claudinine.Tests;

/// <summary>
/// The per-session retrieval launcher (run.sh / run.cmd) written next to the
/// colocated mirror — what makes digest-header retrieval work with no PATH
/// entry (hosted/Cowork installs may not ship a top-level bin/).
/// </summary>
public sealed class LauncherTests : IDisposable
{
    private readonly string _dir;

    public LauncherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "claudinine-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Transcript => Path.Combine(_dir, "test-session.jsonl");
    private string RunSh => Path.Combine(_dir, "test-session", "claudinine", "run.sh");
    private string RunCmd => Path.Combine(_dir, "test-session", "claudinine", "run.cmd");

    [Test]
    public async Task WritesBothLaunchersTargetingTheBinary()
    {
        Launcher.EnsureCurrent(Transcript, @"C:\plug\libexec\win-x64\claudinine.exe");

        string sh = File.ReadAllText(RunSh);
        await Assert.That(sh).StartsWith("#!/bin/sh\n");
        // Forward slashes and quoting: the script must survive Git Bash on
        // Windows and paths with spaces.
        await Assert.That(sh).Contains("exec \"C:/plug/libexec/win-x64/claudinine.exe\" \"$@\"");
        await Assert.That(sh).Contains("export CLAUDININE_DIR");
        // A CRLF shebang script is broken on a real /bin/sh.
        await Assert.That(sh).DoesNotContain("\r");

        string cmd = File.ReadAllText(RunCmd);
        await Assert.That(cmd).Contains("\"C:\\plug\\libexec\\win-x64\\claudinine.exe\" %*");
        await Assert.That(cmd).Contains("CLAUDININE_DIR");
    }

    [Test]
    public async Task PluginLayoutTargetsTheRoutingShims()
    {
        // A binary under libexec/<rid>/ bakes the WRITE-time platform; the shim
        // beside it selects the RID from uname at RUN time — the only choice
        // that stays coherent when the hook's OS is not the shell's (cowork B5).
        string libexec = Path.Combine(_dir, "plug", "libexec");
        Directory.CreateDirectory(Path.Combine(libexec, "win-x64"));
        string binary = Path.Combine(libexec, "win-x64", "claudinine.exe");
        File.WriteAllText(binary, "");
        File.WriteAllText(Path.Combine(libexec, "claudinine"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(libexec, "claudinine.cmd"), "@echo off\r\n");

        Launcher.EnsureCurrent(Transcript, binary);

        string libexecFwd = libexec.Replace('\\', '/');
        await Assert.That(File.ReadAllText(RunSh))
            .Contains($"exec \"{libexecFwd}/claudinine\" \"$@\"");
        await Assert.That(File.ReadAllText(RunCmd))
            .Contains($"\"{Path.Combine(libexec, "claudinine.cmd")}\" %*");
    }

    [Test]
    public async Task MissingShimFallsBackToTheBinary()
    {
        // A dev tree or hand-pruned install has no shims: target what provably
        // exists. (WritesBothLaunchersTargetingTheBinary covers the non-existent
        // path case; this pins the layout-matched-but-shimless case.)
        string libexec = Path.Combine(_dir, "plug2", "libexec");
        Directory.CreateDirectory(Path.Combine(libexec, "linux-x64"));
        string binary = Path.Combine(libexec, "linux-x64", "claudinine");
        File.WriteAllText(binary, "");

        Launcher.EnsureCurrent(Transcript, binary);

        await Assert.That(File.ReadAllText(RunSh))
            .Contains($"exec \"{binary.Replace('\\', '/')}\" \"$@\"");
    }

    [Test]
    public async Task SecondPassIsAByteAndMtimeNoOp()
    {
        Launcher.EnsureCurrent(Transcript, @"C:\plug\claudinine.exe");
        var past = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(RunSh, past);
        File.SetLastWriteTimeUtc(RunCmd, past);

        Launcher.EnsureCurrent(Transcript, @"C:\plug\claudinine.exe");

        await Assert.That(File.GetLastWriteTimeUtc(RunSh)).IsEqualTo(past);
        await Assert.That(File.GetLastWriteTimeUtc(RunCmd)).IsEqualTo(past);
    }

    [Test]
    public async Task RetargetsWhenTheBinaryMoves()
    {
        // A plugin update or a cross-context resume changes the running binary's
        // path; the next pass must repoint the launcher.
        Launcher.EnsureCurrent(Transcript, @"C:\plug\v1\claudinine.exe");
        Launcher.EnsureCurrent(Transcript, @"C:\plug\v2\claudinine.exe");

        string sh = File.ReadAllText(RunSh);
        await Assert.That(sh).Contains("C:/plug/v2/claudinine.exe");
        await Assert.That(sh).DoesNotContain("v1");
    }

    [Test]
    public async Task SubagentTranscriptSharesTheSessionLauncher()
    {
        // Subagent transcripts map to their SESSION's claudinine dir, so one
        // launcher serves the session and every agent.
        string agent = Path.Combine(_dir, "test-session", "subagents", "agent-abc.jsonl");
        await Assert.That(Launcher.PathFor(agent)).IsEqualTo(RunSh);
    }

    [Test]
    public async Task HeaderPathUsesForwardSlashesOnly()
    {
        await Assert.That(Launcher.HeaderPathFor(Transcript)).DoesNotContain("\\");
        await Assert.That(Launcher.HeaderPathFor(Transcript)).EndsWith("/test-session/claudinine/run.sh");
    }

    [Test]
    public async Task CompactionPassWritesTheLauncher()
    {
        // Integration: a real pass leaves run.sh next to the mirror, pointed at
        // the running process (the test host, here — the target is not asserted).
        var b = new TranscriptBuilder().UserPrompt("investigate");
        b.BashRead("sed -n '1,50p' src/a.cs", out _, new string('x', 2000));
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        await Assert.That(File.Exists(RunSh)).IsTrue();
        await Assert.That(File.Exists(RunCmd)).IsTrue();
        await Assert.That(File.ReadAllText(RunSh)).StartsWith("#!/bin/sh\n");
    }

    [Test]
    public async Task FrozenSessionStillGetsALauncher()
    {
        // restore-compaction-off sessions run MirrorOnly; their existing digests
        // still need retrieval to work, so the launcher stays fresh there too.
        var b = new TranscriptBuilder().UserPrompt("hello");
        b.AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.MirrorOnly(path);

        await Assert.That(File.Exists(RunSh)).IsTrue();
    }
}
