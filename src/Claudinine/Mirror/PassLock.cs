namespace Claudinine.Mirror;

/// <summary>
/// Cross-process per-transcript lock: hooks fire concurrently (parallel agents'
/// SubagentStops land together, and Stop can overlap them all), and two passes
/// over the SAME transcript could double-append the mirror — worse, interleave
/// partial writer buffers into corrupt mirror lines. The lock is an exclusive
/// handle on `&lt;stem&gt;.lock` in the colocated dir: Windows enforces it as a
/// sharing violation, Unix as flock, and either way the OS releases it when the
/// process dies, so a crashed hook can never wedge a session.
///
/// Try-acquire only, never wait: the holder is running the same idempotent
/// pass this caller wanted, so busy means the work is already being done —
/// skipping is the optimization, not a loss. Deliberately NO DeleteOnClose:
/// the lock is the open handle, not the file's existence, which sidesteps the
/// unlink/re-create race where a deleted-while-held path lets a third process
/// lock a fresh inode alongside the original holder. The stale `.lock` file is
/// inert; the colocated GC reaps it once its transcript is gone (safe: a pass
/// over a gone transcript is a no-op, so the unlink race has no victim), and
/// the session dir takes the rest under SessionDirGc.
/// </summary>
internal static class PassLock
{
    /// <summary>
    /// The lock, or null when another hook holds it — or when the claudinine
    /// dir cannot be created at all, in which case mirror writes would fail
    /// anyway and skipping the pass is the consistent fail-closed answer.
    /// </summary>
    public static IDisposable? TryAcquire(string transcriptPath)
    {
        try
        {
            string dir = MirrorLocator.ClaudinineDirFor(transcriptPath);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir,
                Path.GetFileNameWithoutExtension(transcriptPath) + ".lock");
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, bufferSize: 1);
        }
        catch
        {
            return null;
        }
    }
}
