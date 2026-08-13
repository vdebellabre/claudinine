using BenchmarkDotNet.Attributes;
using Claudinine.Rules;
using Claudinine.Transcript;

namespace Claudinine.Benchmarks;

/// <summary>
/// Per-rule cost: which of the 16 rules in <see cref="RuleCatalog"/> actually
/// spends the time. This is the breakdown that tells you where to optimize.
///
/// Each rule is measured IN ITS CATALOG POSITION, not on a pristine transcript.
/// The catalog is ordered deliberately (supersession and dedup before the age
/// tiers, the mega-block net last), and later rules read through
/// <c>RuleHelpers.CurrentNode</c>, so they see earlier rules' pending edits.
/// Running rule N against an untouched file would measure a workload that never
/// occurs in production — typically a larger one, since the rules ahead of it
/// have not yet shrunk anything.
/// </summary>
[MemoryDiagnoser]
public class RuleBenchmarks
{
    private string text = "";
    private int ruleIndex;

    /// <summary>
    /// One case per rule, labelled by name so the results table reads as a
    /// ranking. Index is carried along because names are not unique keys in
    /// principle and position is what defines the rule's input state.
    /// </summary>
    public static IEnumerable<string> RuleNames() =>
        RuleCatalog.All.Select(r => r.Name);

    [ParamsSource(nameof(RuleNames))]
    public string? Rule { get; set; }

    /// <summary>
    /// A single representative file. Crossing 16 rules by 4 size tiers would be
    /// 64 cases and a very long run; the per-rule ranking is the question here,
    /// and <see cref="PipelineBenchmarks"/> already covers scaling by size. The
    /// median main transcript is the most representative single input.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        string? dir = Corpus.TryFindCorpus()
            ?? throw new InvalidOperationException(Corpus.DescribeMissing());
        var median = Corpus.Tiers(dir).FirstOrDefault(c => c.Label == "main-median")
            ?? throw new InvalidOperationException("corpus has no main transcripts");
        text = File.ReadAllText(median.File.FullName);
        ruleIndex = Array.FindIndex(RuleCatalog.All, r => r.Name == Rule);
        if (ruleIndex < 0)
            throw new InvalidOperationException($"unknown rule: {Rule}");
    }

    /// <summary>
    /// Rebuild the exact state this rule sees in a real pass: a fresh parse with
    /// every preceding rule already applied. This runs per iteration and is NOT
    /// measured — [IterationSetup] is excluded from the reported time.
    /// </summary>
    private TranscriptFile Prepared()
    {
        var transcript = Harness.ParseFromText(text, "bench.jsonl")
            ?? throw new InvalidOperationException("corpus file failed to parse");
        for (int i = 0; i < ruleIndex; i++)
            RuleCatalog.All[i].Apply(transcript);
        return transcript;
    }

    private TranscriptFile? prepared;

    [IterationSetup]
    public void IterationSetup() => prepared = Prepared();

    [Benchmark]
    public int ApplyRule()
    {
        RuleCatalog.All[ruleIndex].Apply(prepared!);
        return prepared!.Records.Count;
    }
}
