namespace Claudinine;

/// <summary>
/// Opt-in diagnostics, shared by every layer; silent by default (the product
/// stance is install-and-forget). Two independent switches:
/// stderr via the CLAUDININE_DEBUG environment variable, and a durable log
/// file via a marker the user creates — hooks run headless, their stderr is
/// effectively invisible, so the file is the only way to diagnose a silent
/// fail-closed skip after the fact.
/// </summary>
internal static class Dbg
{
    /// <summary>
    /// Read once at startup: the variable is invocation-level configuration, set by
    /// whoever launches the hook, and nothing mutates it in-process. Caching keeps
    /// the <c>catch when (!Dbg.Active)</c> filter in Compactor a constant branch
    /// rather than an environment lookup per rule.
    /// </summary>
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("CLAUDININE_DEBUG") is not null;

    /// <summary>
    /// File sink, enabled by EXISTENCE: create an empty
    /// <c>~/.claude/claudinine-debug.log</c> and every pass appends its
    /// diagnostics there; delete it to opt out. No env var needed — the point
    /// is diagnosing hooks the user cannot re-launch with different
    /// configuration. Mutable only as a test seam (production reads it once,
    /// per-invocation process model).
    /// </summary>
    internal static string? FileSink = ResolveFileSink();

    /// <summary>Any diagnostics on — the rethrow filters key on this, so a
    /// swallowed exception is always one nobody asked to see.</summary>
    public static bool Active => Enabled || FileSink is not null;

    /// <summary>
    /// Growth cap for the file sink: a marker left in place for weeks must not
    /// eat the disk, so appends stop here. The truncated tail is the signal —
    /// a capped file is itself the "rotate me" diagnostic.
    /// </summary>
    private const long FileSinkMaxBytes = 10 * 1024 * 1024;

    public static void Log(string message)
    {
        if (Enabled)
            Console.Error.WriteLine($"[claudinine debug] {message}");
        if (FileSink is not string path)
            return;
        try
        {
            // Open/append/close per line: hook processes run concurrently
            // (parallel SubagentStops + session Stop), so no handle is held and
            // ReadWrite sharing lets simultaneous appenders through. A line
            // torn between two writers costs nothing — this is diagnostics.
            using var stream = new FileStream(
                path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            if (stream.Position > FileSinkMaxBytes)
                return;
            using var writer = new StreamWriter(stream);
            writer.WriteLine(
                $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z [{Environment.ProcessId}] {message}");
        }
        catch
        {
            // The sink must never break the pass it exists to observe.
        }
    }

    private static string? ResolveFileSink()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "claudinine-debug.log");
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null; // no resolvable home: stay silent, like everything else
        }
    }
}
