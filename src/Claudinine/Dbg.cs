namespace Claudinine;

/// <summary>CLAUDININE_DEBUG-gated stderr diagnostics, shared by every layer.</summary>
internal static class Dbg
{
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("CLAUDININE_DEBUG") is not null;

    public static void Log(string message)
    {
        if (Enabled)
            Console.Error.WriteLine($"[claudinine debug] {message}");
    }
}
