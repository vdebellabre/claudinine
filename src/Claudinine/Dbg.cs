namespace Claudinine;

/// <summary>CLAUDININE_DEBUG-gated stderr diagnostics, shared by every layer.</summary>
internal static class Dbg
{
    /// <summary>
    /// Read once at startup: the variable is invocation-level configuration, set by
    /// whoever launches the hook, and nothing mutates it in-process. Caching keeps
    /// the <c>catch when (!Dbg.Enabled)</c> filter in Compactor a constant branch
    /// rather than an environment lookup per rule.
    /// </summary>
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("CLAUDININE_DEBUG") is not null;

    public static void Log(string message)
    {
        if (Enabled)
            Console.Error.WriteLine($"[claudinine debug] {message}");
    }
}
