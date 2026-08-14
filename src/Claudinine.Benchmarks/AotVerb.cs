using System.Globalization;
using System.Text.Json;

namespace Claudinine.Benchmarks;

/// <summary>
/// End-to-end wall-clock measurement of the SHIPPED artifact: the Native AOT
/// binary, invoked as a subprocess exactly as the app invokes it — a hook JSON
/// payload on stdin, one process per event.
///
/// This is the only measurement here that sees what a user actually waits for.
/// `run` and the BenchmarkDotNet suite both measure JIT-compiled code in a warm,
/// long-lived process; production is a cold AOT process that starts, compacts one
/// file, and exits. Startup is therefore not overhead to be excluded — for a small
/// transcript it IS the measurement.
///
/// Three invariants, each of which produced a wrong number before it was fixed:
///
/// 1. It runs on a COPY. Compactor.Run rewrites the transcript in place; pointing
///    this at bench/corpus/ would destroy the baseline on first use.
/// 2. Each timed invocation gets a PRISTINE copy. The pass is idempotent, so a
///    second run over the same file finds its work already done and reports a
///    time that no real hook would ever see.
/// 3. Mirrors are redirected via CLAUDE_PLUGIN_DATA into the temp workspace. The
///    mirror lives outside the transcript directory, so without this the harness
///    would append megabytes into the user's real mirror pool.
/// </summary>
internal static class AotVerb
{
    /// <summary>Hook events worth timing separately, and why they differ.</summary>
    private static readonly (string Event, string What)[] Events =
    [
        ("UserPromptSubmit", "steady state: the per-prompt critical path, session file only"),
        ("SessionStart", "cold open: adds the subagent sweep, mirror GC and session-dir GC"),
    ];

    public static int Run(string[] args)
    {
        string? exe = null;
        string? only = null;
        int limit = int.MaxValue, iterations = 1;
        string? evt = null;
        bool verbose = false, keepWorkspace = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--exe" when i + 1 < args.Length:
                    exe = args[++i];
                    break;
                case "--only" when i + 1 < args.Length:
                    only = args[++i];
                    break;
                case "--event" when i + 1 < args.Length:
                    evt = args[++i];
                    break;
                case "--limit" when i + 1 < args.Length:
                    limit = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--iterations" or "-n" when i + 1 < args.Length:
                    iterations = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--verbose" or "-v":
                    verbose = true;
                    break;
                case "--keep":
                    keepWorkspace = true;
                    break;
                default:
                    Console.Error.WriteLine($"unknown option: {args[i]}");
                    return 1;
            }
        }

        exe ??= FindAotBinary();
        if (exe is null)
        {
            Console.Error.WriteLine(NoBinaryMessage());
            return 1;
        }
        if (!File.Exists(exe))
        {
            Console.Error.WriteLine($"binary not found: {exe}");
            return 1;
        }

        if (only is not (null or "main" or "agent"))
        {
            Console.Error.WriteLine("--only accepts 'main' or 'agent'");
            return 1;
        }
        string? corpus = Corpus.TryFindCorpus();
        if (corpus is null)
        {
            Console.Error.WriteLine(Corpus.DescribeMissing());
            return 1;
        }
        var files = Corpus.All(corpus);
        if (only is not null)
            files = files.Where(f => f.Directory?.Name == only).ToList();
        if (limit < files.Count)
            files = files.Take(limit).ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine("corpus matched no files");
            return 1;
        }

        var events = evt is null
            ? Events
            : Events.Where(e => e.Event.Equals(evt, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (events.Length == 0)
        {
            Console.Error.WriteLine(
                $"unknown event: {evt} (known: {string.Join(", ", Events.Select(e => e.Event))})");
            return 1;
        }

        Console.WriteLine($"binary : {exe}");
        Console.WriteLine($"version: {ProbeVersion(exe)}");
        Console.WriteLine(
            $"corpus : {files.Count} file(s), {Corpus.Human(files.Sum(f => f.Length))}");
        Console.WriteLine();

        string workspace = Path.Combine(
            Path.GetTempPath(), "claudinine-aot-bench-" + Environment.ProcessId);
        try
        {
            foreach ((string name, string what) in events)
            {
                Console.WriteLine($"=== {name} — {what}");
                var results = new List<Invocation>();
                for (int iter = 0; iter < iterations; iter++)
                {
                    foreach (var file in files)
                        results.Add(TimeOne(exe, file, name, workspace, iter));
                }
                Report(results, iterations, verbose);
                Console.WriteLine();
            }
            return 0;
        }
        finally
        {
            if (keepWorkspace)
                Console.WriteLine($"workspace kept: {workspace}");
            else
                TryDeleteDirectory(workspace);
        }
    }

    private readonly record struct Invocation(
        string Name, long InputBytes, long OutputBytes, double Ms, int ExitCode);

    /// <summary>
    /// One hook invocation, timed. The copy and the mirror-dir setup happen
    /// OUTSIDE the stopwatch — only the subprocess is measured, which is exactly
    /// the span the app waits on.
    /// </summary>
    private static Invocation TimeOne(
        string exe, FileInfo source, string hookEvent, string workspace, int iteration)
    {
        // A per-invocation directory: pristine input (invariant 2) and a private
        // mirror pool (invariant 3), both discarded after.
        string cell = Path.Combine(workspace, $"i{iteration}", Path.GetRandomFileName());
        string projects = Path.Combine(cell, "projects");
        Directory.CreateDirectory(projects);
        string transcript = Path.Combine(projects, source.Name);
        source.CopyTo(transcript);

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("hook");
        // Mirrors and load stamps land here, never in the user's real pool.
        psi.Environment["CLAUDE_PLUGIN_DATA"] = Path.Combine(cell, "plugin-data");

        string payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["hook_event_name"] = hookEvent,
            ["transcript_path"] = transcript,
            ["session_id"] = Path.GetFileNameWithoutExtension(source.Name),
        });

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi)!;
        proc.StandardInput.Write(payload);
        proc.StandardInput.Close();
        // Drain both pipes before waiting: a full pipe buffer would deadlock.
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        sw.Stop();

        long after = new FileInfo(transcript).Length;
        if (stderr.Length > 0)
            Console.Error.WriteLine($"  [stderr] {source.Name}: {stderr.Trim()}");
        _ = stdout;

        var result = new Invocation(
            source.Name, source.Length, after, sw.Elapsed.TotalMilliseconds, proc.ExitCode);
        TryDeleteDirectory(cell);
        return result;
    }

    private static void Report(List<Invocation> all, int iterations, bool verbose)
    {
        // Steady state is the last iteration: the first pays OS file-cache misses
        // on 189 MB of corpus, which is a disk measurement, not a code one.
        int perIteration = all.Count / iterations;
        var last = all.Skip((iterations - 1) * perIteration).ToList();

        if (verbose)
        {
            foreach (var r in last.OrderByDescending(r => r.Ms))
            {
                Console.WriteLine(
                    $"  {r.Ms,9:N1} ms  {Corpus.Human(r.InputBytes),9} -> "
                    + $"{Corpus.Human(r.OutputBytes),9}  {r.Name}");
            }
        }

        var ms = last.Select(r => r.Ms).OrderBy(x => x).ToList();
        long inBytes = last.Sum(r => r.InputBytes);
        long outBytes = last.Sum(r => r.OutputBytes);
        var failed = last.Where(r => r.ExitCode != 0).ToList();

        Console.WriteLine($"  invocations : {ms.Count}"
            + (iterations > 1 ? $"  (last of {iterations} iteration(s))" : ""));
        Console.WriteLine($"  total       : {ms.Sum() / 1000.0:N2} s of wall clock");
        Console.WriteLine($"  mean        : {ms.Average(),9:N1} ms per invocation");
        Console.WriteLine($"  median      : {Percentile(ms, 0.50),9:N1} ms");
        Console.WriteLine($"  p95         : {Percentile(ms, 0.95),9:N1} ms");
        Console.WriteLine($"  min / max   : {ms[0]:N1} / {ms[^1]:N1} ms");
        Console.WriteLine($"  bytes       : {Corpus.Human(inBytes)} -> {Corpus.Human(outBytes)}"
            + (inBytes > 0
                ? $"  ({100.0 * (inBytes - outBytes) / inBytes:N1}% smaller — sanity only,"
                    + " eng/bench/compare.py is the token authority)"
                : ""));
        if (failed.Count > 0)
        {
            Console.WriteLine($"  NONZERO EXIT: {failed.Count} invocation(s)");
            foreach (var f in failed.Take(5))
                Console.WriteLine($"    exit {f.ExitCode}: {f.Name}");
        }

        // The floor matters as much as the mean: it is what a hook pays on the
        // smallest real session. Deliberately NOT called "startup" — measured on
        // this machine, `claudinine version` (start, print, exit) is ~12 ms, so
        // the floor is mostly the pass itself, not process creation. Conflating
        // the two invites optimizing the wrong half.
        Console.WriteLine($"  floor       : {ms[0],9:N1} ms on the smallest session"
            + " (pass + process start; compare `claudinine version` for start alone)");
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        double idx = p * (sorted.Count - 1);
        int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
        return lo == hi ? sorted[lo] : sorted[lo] + ((sorted[hi] - sorted[lo]) * (idx - lo));
    }

    /// <summary>
    /// Look where an AOT publish actually lands, newest first. Never falls back to
    /// a JIT build: silently timing `dotnet run` output would defeat the entire
    /// point of this verb.
    /// </summary>
    private static string? FindAotBinary()
    {
        string root = Corpus.TryFindRoot() ?? Directory.GetCurrentDirectory();
        var candidates = new List<string>();
        foreach (string dir in new[]
        {
            // Where the bench publish profile drops it — first because it is the
            // one built to be measured, and it sits beside the corpus it runs on.
            Path.Combine(root, "bench", "bin"),
            Path.Combine(root, "publish"),
            Path.Combine(root, "src", "Claudinine", "bin", "Release"),
        })
        {
            if (Directory.Exists(dir))
                candidates.AddRange(Directory.EnumerateFiles(dir, "claudinine.exe", SearchOption.AllDirectories));
        }
        return candidates
            .Select(p => new FileInfo(p))
            // An AOT binary is self-contained and large; a framework-dependent
            // apphost of the same name is ~140 KB and would measure nothing.
            .Where(f => f.Length > 1_000_000)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.FullName)
            .FirstOrDefault();
    }

    private static string ProbeVersion(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("version");
            using var p = Process.Start(psi)!;
            string v = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return v.Length > 0 ? v : "(unknown)";
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            return "(unknown)";
        }
    }

    private static string NoBinaryMessage() =>
        """
        No Native AOT binary found.

        Publish one first — the bench profile drops it in bench/bin/, which is
        where this verb looks before anywhere else:

          dotnet publish src/Claudinine/Claudinine.csproj -c Release -r win-x64 -o bench/bin

        AOT linking needs a working platform linker ("Desktop development with
        C++"); without it ILCompiler fails at the link step rather than here.

        Or point at one built elsewhere — a release archive download works, and is
        the closest thing to what users actually run:

          ... aot --exe path/to/claudinine.exe

        Deliberately no JIT fallback: timing a JIT build here would silently
        answer a different question than the one this verb exists to ask.
        """;

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup is best-effort; a locked file must not fail the run.
        }
    }
}
