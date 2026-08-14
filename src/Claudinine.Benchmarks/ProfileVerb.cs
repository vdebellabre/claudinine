using System.Diagnostics;
using System.Globalization;
using Claudinine.Rules;
using Claudinine.Transcript;

namespace Claudinine.Benchmarks;

/// <summary>
/// On-demand full-corpus compaction, in-process — the profiler target.
///
/// Unlike the BenchmarkDotNet suite, this runs everything in ONE process with no
/// spawned children, no JIT-warmup orchestration and no generated runner
/// assembly. That matters because it is meant to be launched under the Visual
/// Studio profiler (Debug > Performance Profiler, or "Analyze > Performance
/// Profiler" with this project as the startup project): every sample lands in
/// our own call tree, attributable to a rule.
///
/// Two modes, sharing their vocabulary with the `aot` verb:
///
///   --full    every measured pass parses the pristine, uncompacted text — the
///             workload of a fresh or resumed session.
///   --steady  settle each file once (untimed), then measure passes over the
///             settled text — the workload of prompt N once 1..N-1 are done.
///
/// The mode is REQUIRED. The two numbers differ several-fold and quoting one
/// for the other is the recurring trap in the notes file, so a bare `profile`
/// refuses to guess.
/// </summary>
internal static class ProfileVerb
{
    public static int Run(string[] args)
    {
        var options = ProfileOptions.Parse(args);
        if (options.Error is not null)
        {
            Console.Error.WriteLine(options.Error);
            return 1;
        }

        string? corpus = Corpus.TryFindCorpus();
        if (corpus is null)
        {
            Console.Error.WriteLine(Corpus.DescribeMissing());
            return 1;
        }

        var files = Corpus.All(corpus);
        if (options.Only is string only)
            files = files.Where(f => f.Directory?.Name == only).ToList();
        if (options.Limit is int limit && limit < files.Count)
            files = files.Take(limit).ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine("no corpus files matched.");
            return 1;
        }

        Console.WriteLine($"Compacting {files.Count} transcript(s), {options.Iterations} iteration(s), "
            + (options.Steady ? "STEADY mode (settled input)" : "FULL mode (uncompacted input)")
            + (options.WarmUp ? ", after a warm-up pass" : "") + ".");

        // One pass over the corpus is only ~3 s of CPU. A ~1 kHz sampling
        // profiler gets a few thousand samples for that, and since one rule
        // takes roughly half of them the cheap rules land inside the noise
        // band — their ranking would not be trustworthy. Say so rather than
        // letting a thin profile look authoritative.
        if (options.Iterations == 1)
        {
            Console.WriteLine(
                "note: a single pass is ~3 s of CPU — thin for a sampling profiler."
                + " Use -n 20 for a CPU profile.");
        }
        Console.WriteLine();

        // Read every file up front. Disk time is not what we are profiling, and
        // hoisting it out keeps the profiled region pure compute — otherwise the
        // first pass over a 15 MB file is dominated by cold-cache reads and the
        // call tree is mostly FileStream.
        var inputs = new List<(FileInfo File, string Text)>(files.Count);
        long totalBytes = 0;
        foreach (var f in files)
        {
            inputs.Add((f, File.ReadAllText(f.FullName)));
            totalBytes += f.Length;
        }
        Console.WriteLine($"Loaded {Corpus.Human(totalBytes)} into memory.");

        if (options.Steady)
        {
            // Settle each file with one untimed pass so the measured passes see
            // the text a live session's transcript is actually in. This is the
            // in-memory analog of `aot --steady`'s warm invocation, and it also
            // JIT-warms the whole pipeline, so --warmup adds nothing here.
            for (int i = 0; i < inputs.Count; i++)
                inputs[i] = (inputs[i].File, Settle(inputs[i].Text, inputs[i].File.FullName));
            Console.WriteLine("Settling pass complete (untimed; also serves as JIT warm-up).");
        }

        if (options.WarmUp && !options.Steady)
        {
            // Force JIT of the whole pipeline before the measured region, so
            // first-call compilation cost is not attributed to whichever rule
            // happened to run first.
            foreach (var (file, text) in inputs)
                CompactOnce(text, file.FullName);
            Console.WriteLine("Warm-up pass complete.");
        }

        Console.WriteLine();
        var results = new List<FileResult>(inputs.Count);
        var overall = Stopwatch.StartNew();

        for (int iter = 0; iter < options.Iterations; iter++)
        {
            results.Clear();
            foreach (var (file, text) in inputs)
                results.Add(Measure(file, text, options.Verbose));
        }

        overall.Stop();
        Report(results, overall.Elapsed, totalBytes, options);
        return 0;
    }

    /// <summary>One pass, kept as its own method so it shows up as a single frame.</summary>
    private static int CompactOnce(string text, string path)
    {
        var transcript = Harness.ParseFromText(text, path);
        if (transcript is null)
            return 0;
        foreach (var rule in RuleCatalog.All)
            rule.Apply(transcript);
        return Harness.SerializeAndValidate(transcript);
    }

    /// <summary>
    /// One untimed compaction, returning the text the pass would have written —
    /// the "file at rest" input that steady mode measures against. Falls back to
    /// the original text when the pass parses nothing, changes nothing, or the
    /// rewrite is refused, which matches what production leaves on disk in each
    /// of those cases.
    /// </summary>
    private static string Settle(string text, string path)
    {
        var transcript = Harness.ParseFromText(text, path);
        if (transcript is null)
            return text; // Measure() will surface the parse failure per file.
        foreach (var rule in RuleCatalog.All)
            rule.Apply(transcript);
        if (!transcript.HasChanges)
            return text;
        var lines = transcript.TryComputeRewrite();
        return lines is null ? text : string.Join('\n', lines) + "\n";
    }

    private static FileResult Measure(FileInfo file, string text, bool verbose)
    {
        var sw = Stopwatch.StartNew();
        var transcript = Harness.ParseFromText(text, file.FullName);
        if (transcript is null)
        {
            sw.Stop();
            // A corpus file that will not parse is a real finding (the loader is
            // the format sentinel), not something to hide behind a zero row.
            return new FileResult(file, sw.Elapsed, file.Length, file.Length, 0, Failed: true);
        }
        long before = Harness.RewrittenLength(transcript);

        foreach (var rule in RuleCatalog.All)
            rule.Apply(transcript);

        int changed = transcript.Records.Count(r => r.Replacement is not null || r.Removed);
        Harness.SerializeAndValidate(transcript);
        long after = Harness.RewrittenLength(transcript);
        sw.Stop();

        if (verbose)
        {
            double pct = before == 0 ? 0 : 100.0 * (before - after) / before;
            Console.WriteLine(
                $"  {file.Name,-45} {sw.Elapsed.TotalMilliseconds,8:F1} ms  "
                + $"{Corpus.Human(before),9} -> {Corpus.Human(after),9}  ({pct,5:F1}% bytes)");
        }
        return new FileResult(file, sw.Elapsed, before, after, changed, Failed: false);
    }

    private static void Report(List<FileResult> results, TimeSpan wall, long totalBytes, ProfileOptions options)
    {
        var ok = results.Where(r => !r.Failed).ToList();
        var failed = results.Where(r => r.Failed).ToList();

        long before = ok.Sum(r => r.BytesBefore);
        long after = ok.Sum(r => r.BytesAfter);
        double totalMs = ok.Sum(r => r.Elapsed.TotalMilliseconds);

        Console.WriteLine();
        Console.WriteLine("--- summary ---------------------------------------------");
        Console.WriteLine($"mode             : {(options.Steady ? "steady (settled input)" : "full (uncompacted input)")}");
        Console.WriteLine($"files            : {ok.Count}"
            + (failed.Count > 0 ? $"  ({failed.Count} FAILED TO PARSE)" : ""));
        Console.WriteLine($"iterations       : {options.Iterations}");
        Console.WriteLine($"corpus size      : {Corpus.Human(totalBytes)}");
        Console.WriteLine($"wall clock       : {wall.TotalSeconds:F2} s (all {options.Iterations} iteration(s))");

        // Every stat below this line describes the LAST iteration only. With
        // --warmup that is a fully warmed steady-state pass, which is the number
        // worth quoting; folding in the earlier (colder) iterations would only
        // blur it. Said explicitly because wall clock above covers all of them,
        // so at -n 5 the two figures differ ~5x and would otherwise read as a bug.
        Console.WriteLine($"pass time        : {totalMs:F0} ms  <- last iteration, the warmed number");
        if (options.Iterations > 1)
            Console.WriteLine($"       mean/iter : {wall.TotalMilliseconds / options.Iterations:F0} ms (incl. cold first pass)");

        if (ok.Count > 0)
        {
            var sorted = ok.OrderBy(r => r.Elapsed).ToList();
            Console.WriteLine("per file (last iteration):");
            Console.WriteLine($"          mean   : {totalMs / ok.Count:F1} ms");
            Console.WriteLine($"          median : {sorted[sorted.Count / 2].Elapsed.TotalMilliseconds:F1} ms");
            Console.WriteLine($"          p95    : {sorted[(int)(sorted.Count * 0.95)].Elapsed.TotalMilliseconds:F1} ms");
            Console.WriteLine($"          max    : {sorted[^1].Elapsed.TotalMilliseconds:F1} ms"
                + $"  ({sorted[^1].File.Name}, {Corpus.Human(sorted[^1].File.Length)})");
            double mbPerSec = totalMs == 0 ? 0 : (before / (1024.0 * 1024)) / (totalMs / 1000.0);
            Console.WriteLine($"throughput       : {mbPerSec:F1} MB/s");
        }

        if (options.Steady)
        {
            // The input was settled before timing began, so a byte reduction here
            // does not mean "compaction worked" — it means the settling premise
            // BROKE and the timed passes did real work. Same guard as
            // `aot --steady` and eng/bench/steady.py.
            int churned = ok.Count(r => r.BytesAfter != r.BytesBefore);
            Console.WriteLine($"at rest          : {ok.Count - churned}/{ok.Count} file(s)"
                + (churned > 0
                    ? $"  WARNING: {churned} still shrinking — those timings include"
                        + " real compaction, not steady-state work"
                    : "  (none shrank further — the steady-state premise holds)"));
        }
        else if (before > 0)
        {
            // Byte saving is reported only as a sanity signal that the pass did
            // real work under the profiler. It is NOT the effectiveness number:
            // that is measured in tokens by eng/bench/compare.py, and bytes
            // understate the token saving by roughly 3.5x through JSON envelope
            // dilution.
            Console.WriteLine($"bytes            : {Corpus.Human(before)} -> {Corpus.Human(after)}"
                + $"  ({100.0 * (before - after) / before:F1}% smaller; sanity signal only,"
                + " see eng/bench for the token number)");
        }
        Console.WriteLine($"records changed  : {ok.Sum(r => r.RecordsChanged)}");

        if (failed.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("FAILED TO PARSE (corpus or loader problem):");
            foreach (var f in failed)
                Console.WriteLine($"  {f.File.FullName}");
        }

        var slowest = ok.OrderByDescending(r => r.Elapsed).Take(5).ToList();
        if (slowest.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("slowest files:");
            foreach (var r in slowest)
            {
                Console.WriteLine($"  {r.Elapsed.TotalMilliseconds,8:F1} ms  "
                    + $"{Corpus.Human(r.File.Length),9}  {r.File.Name}");
            }
        }
    }

    private sealed record FileResult(
        FileInfo File, TimeSpan Elapsed, long BytesBefore, long BytesAfter,
        int RecordsChanged, bool Failed);

    private sealed class ProfileOptions
    {
        public int Iterations { get; private set; } = 1;
        public int? Limit { get; private set; }
        public string? Only { get; private set; }
        public bool Verbose { get; private set; }
        public bool WarmUp { get; private set; }
        public bool Steady { get; private set; }
        public string? Error { get; private set; }

        public static ProfileOptions Parse(string[] args)
        {
            var o = new ProfileOptions();
            bool? full = null;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--full":
                        full = true;
                        break;
                    case "--steady":
                        o.Steady = true;
                        break;
                    case "--iterations" or "-n" when i + 1 < args.Length:
                        if (!int.TryParse(args[++i], CultureInfo.InvariantCulture, out int n) || n < 1)
                            return Fail(o, "--iterations needs a positive integer");
                        o.Iterations = n;
                        break;
                    case "--limit" when i + 1 < args.Length:
                        if (!int.TryParse(args[++i], CultureInfo.InvariantCulture, out int l) || l < 1)
                            return Fail(o, "--limit needs a positive integer");
                        o.Limit = l;
                        break;
                    case "--only" when i + 1 < args.Length:
                        string only = args[++i];
                        if (only is not ("main" or "agent"))
                            return Fail(o, "--only accepts 'main' or 'agent'");
                        o.Only = only;
                        break;
                    case "--verbose" or "-v":
                        o.Verbose = true;
                        break;
                    case "--warmup":
                        o.WarmUp = true;
                        break;
                    default:
                        return Fail(o, $"unknown option: {args[i]}");
                }
            }
            if (full == true && o.Steady)
                return Fail(o, "--full and --steady are mutually exclusive");
            if (full is null && !o.Steady)
            {
                return Fail(o,
                    "profile needs a mode: --full (uncompacted input, fresh/resumed-session"
                    + " workload) or --steady (settled input, per-prompt workload)."
                    + " The numbers differ several-fold, so no default is guessed.");
            }
            return o;
        }

        private static ProfileOptions Fail(ProfileOptions o, string message)
        {
            o.Error = message;
            return o;
        }
    }
}
