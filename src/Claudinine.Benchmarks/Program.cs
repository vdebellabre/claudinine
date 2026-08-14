using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Claudinine.Benchmarks;

int exitCode = args switch
{
    ["run", .. var runArgs] => RunVerb.Run(runArgs),
    ["aot", .. var aotArgs] => AotVerb.Run(aotArgs),
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
        usage: Claudinine.Benchmarks <run|aot|bench> [options]

          run     Compact the whole corpus once, in-process. This is the
                  profiler target — launch it under the Visual Studio
                  Performance Profiler to get a per-rule call tree.

                    --iterations, -n N   repeat the corpus N times (default 1)
                    --limit N            only the N smallest files
                    --only main|agent    restrict to one corpus half
                    --warmup             unmeasured JIT-warming pass first
                    --verbose, -v        per-file timing lines

          aot     Wall-clock the SHIPPED Native AOT binary, invoked as a
                  subprocess with a hook payload on stdin — one process per
                  event, exactly as the app runs it. Includes process startup,
                  which `run` and `bench` (warm, in-process, JIT) both exclude.
                  Operates on a throwaway copy; never touches bench/corpus.

                  By default this is the COLD pass: every invocation gets a
                  pristine, uncompacted transcript. That is the worst case — a
                  fresh or resumed session. Use --steady for the common case.

                    --exe PATH           binary to time (default: newest AOT
                                         publish found under bench/bin, publish/
                                         or src/.../bin/Release)
                    --event NAME         only UserPromptSubmit, or SessionStart
                    --steady [N]         steady state instead of cold: warm each
                                         file once (untimed), then time N passes
                                         over the settled file (default 3).
                                         Matches eng/bench/steady.py. Several
                                         times faster than cold — not the same
                                         number, do not compare the two.
                    --iterations, -n N   repeat the corpus N times (default 1)
                    --limit N            only the N smallest files
                    --only main|agent    restrict to one corpus half
                    --keep               keep the temp workspace for inspection
                    --verbose, -v        per-invocation timing lines

          bench   Run the BenchmarkDotNet suite (statistically rigorous, slow).
                  Extra args pass through to BenchmarkDotNet, e.g.

                    bench --filter *Pipeline*
                    bench --job short
                    bench --list flat

        Both need the corpus at <repo>/bench/corpus (see eng/bench/README.md).
        """);
    return 1;
}
