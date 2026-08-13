using BenchmarkDotNet.Attributes;
using Claudinine.Rules;
using Claudinine.Transcript;

namespace Claudinine.Benchmarks;

/// <summary>
/// End-to-end cost of one compaction pass, split into the three phases that
/// consume the wall-clock budget: parse, run the rule catalog, serialize +
/// validate the result.
///
/// Everything here is deliberately in-memory. The real <see cref="Compactor"/>
/// also appends to the mirror and atomically swaps the file, but those are
/// filesystem costs that swamp and obscure the compute they surround, and they
/// vary with the machine's disk rather than with our code. The `profile` verb
/// measures the true end-to-end cost including I/O; this measures what we can
/// actually optimize.
/// </summary>
[MemoryDiagnoser]
public class PipelineBenchmarks
{
    private CorpusCase[] cases = [];
    private string text = "";

    /// <summary>
    /// The corpus files, resolved once. Populated by <see cref="Setup"/> rather
    /// than a static initializer so a missing corpus fails with our diagnostic
    /// instead of a TypeInitializationException from inside BenchmarkDotNet.
    /// </summary>
    public IEnumerable<CorpusCase> Cases()
    {
        string? dir = Corpus.TryFindCorpus();
        if (dir is null)
            throw new InvalidOperationException(Corpus.DescribeMissing());
        cases = Corpus.Tiers(dir).ToArray();
        return cases;
    }

    [ParamsSource(nameof(Cases))]
    public CorpusCase? Case { get; set; }

    /// <summary>
    /// Read the file ONCE into memory. Every iteration then re-parses from this
    /// string, so no benchmark is measuring disk read time or OS cache state.
    /// </summary>
    [GlobalSetup]
    public void Setup() => text = File.ReadAllText(Case!.File.FullName);

    [Benchmark(Description = "parse only")]
    public int Parse() => ParseFresh().Records.Count;

    /// <summary>
    /// The full pass minus I/O: parse, every rule, then build+validate the
    /// rewritten text. This is the headline "what does compacting a session
    /// cost" number.
    /// </summary>
    [Benchmark(Description = "parse + rules + rewrite", Baseline = true)]
    public int FullPipeline()
    {
        var transcript = ParseFresh();
        foreach (var rule in RuleCatalog.All)
            rule.Apply(transcript);
        return Harness.SerializeAndValidate(transcript);
    }

    /// <summary>The rule catalog alone, on an already-parsed transcript.</summary>
    [Benchmark(Description = "rules only")]
    public int Rules()
    {
        var transcript = ParseFresh();
        foreach (var rule in RuleCatalog.All)
            rule.Apply(transcript);
        return transcript.Records.Count;
    }

    /// <summary>
    /// Re-parse from the cached text. Load is destructive-by-consequence: rules
    /// mark records via Replacement/Removed, so reusing one TranscriptFile would
    /// have iteration 2 measuring an already-compacted input — which is both the
    /// wrong workload and silently much faster. A fresh parse per iteration is
    /// the only honest option; its cost is isolated by the "parse only" case
    /// above so it can be subtracted when reading the rule numbers.
    /// </summary>
    private TranscriptFile ParseFresh() =>
        Harness.ParseFromText(text, Case!.File.FullName)
        ?? throw new InvalidOperationException($"corpus file failed to parse: {Case!.File.FullName}");
}
