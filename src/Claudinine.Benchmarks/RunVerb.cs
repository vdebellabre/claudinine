using System.Diagnostics;
using System.Globalization;
using Claudinine.Rules;
using Claudinine.Transcript;

namespace Claudinine.Benchmarks;

/// <summary>
/// On-demand full-corpus compaction — the profiler target.
///
/// Unlike the BenchmarkDotNet suite, this runs everything in ONE process with no
/// spawned children, no JIT-warmup orchestration and no generated runner
/// assembly. That matters because it is meant to be launched under the Visual
/// Studio profiler (Debug > Performance Profiler, or "Analyze > Performance
/// Profiler" with this project as the startup project): every sample lands in
/// our own call tree, attributable to a rule.
///
/// It processes all 174 corpus files by default, which is a large enough
/// workload that sampling profilers get meaningful counts without any looping.
/// </summary>
internal static class RunVerb
{
    public static int Run(string[] args)
    {
        var options = RunOptions.Parse(args);
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

        Console.WriteLine($"Compacting {files.Count} transcript(s), {options.Iterations} iteration(s)"
            + (options.WarmUp ? ", after a warm-up pass" : "") + ".");
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

        if (options.WarmUp)
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

    private static void Report(List<FileResult> results, TimeSpan wall, long totalBytes, RunOptions options)
    {
        var ok = results.Where(r => !r.Failed).ToList();
        var failed = results.Where(r => r.Failed).ToList();

        long before = ok.Sum(r => r.BytesBefore);
        long after = ok.Sum(r => r.BytesAfter);
        double totalMs = ok.Sum(r => r.Elapsed.TotalMilliseconds);

        Console.WriteLine();
        Console.WriteLine("--- summary ---------------------------------------------");
        Console.WriteLine($"files            : {ok.Count}"
            + (failed.Count > 0 ? $"  ({failed.Count} FAILED TO PARSE)" : ""));
        Console.WriteLine($"iterations       : {options.Iterations}");
        Console.WriteLine($"corpus size      : {Corpus.Human(totalBytes)}");
        Console.WriteLine($"wall clock       : {wall.TotalSeconds:F2} s");
        Console.WriteLine($"compaction time  : {totalMs:F0} ms (last iteration, sum of per-file)");

        if (ok.Count > 0)
        {
            var sorted = ok.OrderBy(r => r.Elapsed).ToList();
            Console.WriteLine($"per file  mean   : {totalMs / ok.Count:F1} ms");
            Console.WriteLine($"          median : {sorted[sorted.Count / 2].Elapsed.TotalMilliseconds:F1} ms");
            Console.WriteLine($"          p95    : {sorted[(int)(sorted.Count * 0.95)].Elapsed.TotalMilliseconds:F1} ms");
            Console.WriteLine($"          max    : {sorted[^1].Elapsed.TotalMilliseconds:F1} ms"
                + $"  ({sorted[^1].File.Name}, {Corpus.Human(sorted[^1].File.Length)})");
            double mbPerSec = totalMs == 0 ? 0 : (before / (1024.0 * 1024)) / (totalMs / 1000.0);
            Console.WriteLine($"throughput       : {mbPerSec:F1} MB/s");
        }

        // Byte saving is reported only as a sanity signal that the pass did real
        // work under the profiler. It is NOT the effectiveness number: that is
        // measured in tokens by eng/bench/compare.py, and bytes understate the
        // token saving by roughly 3.5x through JSON envelope dilution.
        if (before > 0)
        {
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

    private sealed class RunOptions
    {
        public int Iterations { get; private set; } = 1;
        public int? Limit { get; private set; }
        public string? Only { get; private set; }
        public bool Verbose { get; private set; }
        public bool WarmUp { get; private set; }
        public string? Error { get; private set; }

        public static RunOptions Parse(string[] args)
        {
            var o = new RunOptions();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
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
            return o;
        }

        private static RunOptions Fail(RunOptions o, string message)
        {
            o.Error = message;
            return o;
        }
    }
}
