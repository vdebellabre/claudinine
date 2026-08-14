using System.Globalization;

namespace Claudinine.Benchmarks;

/// <summary>
/// Locates the benchmark corpus and picks the fixed input set.
///
/// The corpus (<c>bench/corpus/</c>) is gitignored real session data — see
/// eng/bench/README.md. It is therefore NOT available to someone who merely
/// clones the repo, and nothing here may pretend otherwise: a missing corpus
/// produces a clear diagnostic, never a synthetic stand-in. Synthetic
/// transcripts would compact at rule hit-rates unlike anything real, so a
/// number measured on them is not comparable to one measured here, and mixing
/// the two silently would be worse than having no number at all.
/// </summary>
public static class Corpus
{
    /// <summary>
    /// Walk up from the running assembly to the repo root (the directory holding
    /// <c>bench/</c> next to <c>src/</c>). Beats a relative path because the
    /// working directory differs between `dotnet run`, a BenchmarkDotNet-spawned
    /// child process, and the VS profiler.
    /// </summary>
    public static string? TryFindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "bench", "corpus"))
                && Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    public static string? TryFindCorpus()
    {
        string? root = TryFindRoot();
        return root is null ? null : Path.Combine(root, "bench", "corpus");
    }

    /// <summary>Every transcript in the corpus, main and agent, ordered by size.</summary>
    public static IReadOnlyList<FileInfo> All(string corpusDir)
    {
        var files = new List<FileInfo>();
        foreach (string kind in new[] { "main", "agent" })
        {
            string dir = Path.Combine(corpusDir, kind);
            if (Directory.Exists(dir))
                files.AddRange(new DirectoryInfo(dir).GetFiles("*.jsonl"));
        }
        files.Sort((a, b) => a.Length.CompareTo(b.Length));
        return files;
    }

    /// <summary>
    /// The micro-benchmark input set: one real file per size tier.
    ///
    /// BenchmarkDotNet runs each case many times, so the whole 191 MB corpus is
    /// out of the question — the largest single file is 15 MB. Tiers instead
    /// answer the question that actually matters for a hook that runs on every
    /// turn: how does cost scale as a session grows? Picking by percentile keeps
    /// the selection stable and self-documenting rather than hard-coding session
    /// ids that are meaningless to a reader (and private besides).
    /// </summary>
    public static IReadOnlyList<CorpusCase> Tiers(string corpusDir)
    {
        var main = All(corpusDir).Where(f => f.Directory?.Name == "main").ToList();
        var agent = All(corpusDir).Where(f => f.Directory?.Name == "agent").ToList();

        var cases = new List<CorpusCase>();
        if (main.Count > 0)
        {
            cases.Add(new CorpusCase("main-small", main[main.Count / 10]));
            cases.Add(new CorpusCase("main-median", main[main.Count / 2]));
            cases.Add(new CorpusCase("main-large", main[^1]));
        }
        // A subagent transcript is one long uninterrupted tool chain, so
        // chain-collapse behaves completely differently on it than on a main
        // file. Reporting a median agent case keeps that path visible.
        if (agent.Count > 0)
            cases.Add(new CorpusCase("agent-median", agent[agent.Count / 2]));
        return cases;
    }

    public static string DescribeMissing() =>
        $"""
        Benchmark corpus not found (expected <repo>/bench/corpus/).

        bench/ is gitignored: it holds real session transcripts (private data),
        so it is never committed. To build it on your own machine:

            python eng/bench/curate.py

        See eng/bench/README.md. Benchmarks are not run against synthetic
        transcripts, because those compact at unrealistic rates and the number
        would not be comparable to a real one.
        """;

    public static string Human(long bytes) => bytes switch
    {
        >= 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        >= 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
    };
}

/// <summary>
/// One benchmark input: a tier label and the real file backing it. Public
/// because BenchmarkDotNet reflects over it as a [ParamsSource] element type,
/// and a public benchmark class may not expose an internal type in its members.
/// </summary>
public sealed record CorpusCase(string Label, FileInfo File)
{
    /// <summary>What BenchmarkDotNet prints in the Params column.</summary>
    public override string ToString() => $"{Label} ({Corpus.Human(File.Length)})";
}
