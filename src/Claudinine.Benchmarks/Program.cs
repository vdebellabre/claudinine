using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Claudinine.Benchmarks;

int exitCode = args switch
{
    ["profile", .. var profileArgs] => ProfileVerb.Run(profileArgs),
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
        usage: Claudinine.Benchmarks <profile|aot|bench> --full|--steady [options]

        `profile` and `aot` both REQUIRE a mode, because the two workloads differ
        several-fold and quoting one for the other is the recurring trap:

          --full     uncompacted input: the fresh/resumed-session workload,
                     the worst case.
          --steady   input settled by one untimed pass first: the per-prompt
                     workload, the common case.

          profile  Compact the whole corpus in-process (JIT, one long-lived
                   process). This is the profiler target — launch it under the
                   Visual Studio Performance Profiler for a per-rule call tree.

                    --iterations, -n N   repeat the corpus N times (default 1)
                    --limit N            only the N smallest files
                    --only main|agent    restrict to one corpus half
                    --warmup             unmeasured JIT-warming pass first
                                         (--steady's settling pass already
                                         warms, so only useful with --full)
                    --verbose, -v        per-file timing lines

          aot      Wall-clock the SHIPPED Native AOT binary, invoked as a
                   subprocess with a hook payload on stdin — one process per
                   event, exactly as the app runs it. Includes process startup,
                   which `profile` and `bench` (warm, in-process, JIT) both
                   exclude. Operates on a throwaway copy; never touches
                   bench/corpus. --steady here matches eng/bench/steady.py.

                    --exe PATH           binary to time (default: newest AOT
                                         publish found under bench/bin, publish/
                                         or src/.../bin/Release)
                    --event NAME         only UserPromptSubmit, or SessionStart
                    --steady [N]         optional pass count per file (default 3;
                                         the median is reported)
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
