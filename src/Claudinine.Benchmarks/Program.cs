using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Claudinine.Benchmarks;

int exitCode = args switch
{
    ["run", .. var runArgs] => RunVerb.Run(runArgs),
    ["bench", .. var benchArgs] => Bench(benchArgs),
    _ => Usage(),
};

WaitForKeyIfInteractive();
return exitCode;

/// <summary>
/// Hold the console open so a profiler launch (VS starts the process in its own
/// window and closes it on exit) does not flash the summary away unread.
///
/// Guarded three ways, because an unconditional ReadKey would be worse than no
/// ReadKey at all: with stdin redirected — a pipe, a CI step, `| head` — the
/// call throws InvalidOperationException, and without a console at all it would
/// block forever. Only an interactive session waits.
/// </summary>
static void WaitForKeyIfInteractive()
{
    if (Console.IsInputRedirected || Console.IsOutputRedirected)
        return;
    try
    {
        Console.WriteLine();
        Console.Write("Press any key to close...");
        Console.ReadKey(intercept: true);
        Console.WriteLine();
    }
    catch (InvalidOperationException)
    {
        // No console attached to read from; nothing to wait for.
    }
}

static int Bench(string[] args)
{
    // Hand the remaining args to BenchmarkDotNet so its own switcher options
    // still work (--filter, --job short, --list, --exporters, ...).
    BenchmarkSwitcher
        .FromTypes([typeof(PipelineBenchmarks), typeof(RuleBenchmarks)])
        .Run(args, DefaultConfig.Instance.WithOptions(ConfigOptions.JoinSummary));
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine(
        """
        usage: Claudinine.Benchmarks <run|bench> [options]

          run     Compact the whole corpus once, in-process. This is the
                  profiler target — launch it under the Visual Studio
                  Performance Profiler to get a per-rule call tree.

                    --iterations, -n N   repeat the corpus N times (default 1)
                    --limit N            only the N smallest files
                    --only main|agent    restrict to one corpus half
                    --warmup             unmeasured JIT-warming pass first
                    --verbose, -v        per-file timing lines

          bench   Run the BenchmarkDotNet suite (statistically rigorous, slow).
                  Extra args pass through to BenchmarkDotNet, e.g.

                    bench --filter *Pipeline*
                    bench --job short
                    bench --list flat

        Both need the corpus at <repo>/bench/corpus (see eng/bench/README.md).
        """);
    return 1;
}
