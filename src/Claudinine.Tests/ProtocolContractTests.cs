using System.Text.Json;

namespace Claudinine.Tests;

/// <summary>
/// Pins the two implicit cross-file contracts the string protocol lives on
/// (see <see cref="Protocol"/>'s class doc for the emitter/matcher matrix):
/// every retrieval form a header emitter writes must be recognized by every
/// matcher — including ForkHealRule's regex, which cannot be composed from the
/// Protocol constants — and the RuleCatalog order constraints documented in
/// its comments must hold structurally. A failure here means an emitter and a
/// matcher drifted apart, the exact class of breakage the constants exist to
/// prevent.
/// </summary>
public sealed class ProtocolContractTests
{
    private const string Sid = "0d0fab12-ed58-4dc5-9788-9e1a58fc9c83";
    // A space in the path on purpose: the launcher form exists precisely
    // because quoted spacey paths must survive.
    private const string Launcher =
        "C:/Users/some one/.claude/projects/p/" + Sid + "/claudinine/run.sh";

    private static readonly JsonSerializerOptions RelaxedEscaping = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Test]
    public async Task LauncherFormIsRecognizedByEveryMatcher()
    {
        string block = ChainCollapseRule.CommandLines(Sid, Launcher);

        // The fragment CarrierHeaderDedupRule and CloneVerb key on.
        await Assert.That(block).Contains(Protocol.LauncherGetFragment + Sid);
        // CloneVerb additionally rewrites the launcher path's directory component.
        await Assert.That(block).Contains($"/{Sid}/claudinine/run.sh");

        // ForkHealRule's regex extracts the sid from every command line.
        var matches = ForkHealRule.GetCommand().Matches(block);
        await Assert.That(matches.Count).IsGreaterThan(0);
        await Assert.That(matches.All(m => m.Groups[1].Value == Sid)).IsTrue();

        // CarrierHeaderDedupRule's sid parse.
        await Assert.That(CarrierHeaderDedupRule.ParseSessionId(block)).IsEqualTo(Sid);

        // Compactor.MirrorLost scans the RAW jsonl line, where the launcher
        // path's closing quote is JSON-escaped — the escaped constant must
        // match what the app's writer actually produces. That writer is Node's
        // JSON.stringify, which escapes a quote as backslash-quote;
        // System.Text.Json's DEFAULT encoder emits the u0022 escape instead,
        // so the relaxed encoder is the faithful stand-in here.
        string rawLine = JsonSerializer.Serialize(block, RelaxedEscaping);
        await Assert.That(rawLine).Contains(Protocol.LauncherGetFragmentJsonEscaped + Sid);
    }

    [Test]
    public async Task LocalModeFormIsRecognizedByEveryMatcher()
    {
        string block = ChainCollapseRule.LocalCommandLines(Sid, "C:/p/outputs/.claudinine/refs");

        await Assert.That(block).Contains(Protocol.MirrorKeyPrefix + Sid);

        var matches = ForkHealRule.GetCommand().Matches(block);
        await Assert.That(matches.Count).IsGreaterThan(0);
        await Assert.That(matches.All(m => m.Groups[1].Value == Sid)).IsTrue();

        await Assert.That(CarrierHeaderDedupRule.ParseSessionId(block)).IsEqualTo(Sid);
    }

    /// <summary>
    /// No current emitter writes the bare form, but 0.1.x–0.4.x transcripts
    /// carry it forever — matchers may gain forms, never drop one.
    /// </summary>
    [Test]
    public async Task BareLegacyFormIsRecognizedByEveryMatcher()
    {
        string legacy = "  " + Protocol.BareGetCommand + Sid + " --ref REF --grep PATTERN";

        var match = ForkHealRule.GetCommand().Match(legacy);
        await Assert.That(match.Success).IsTrue();
        await Assert.That(match.Groups[1].Value).IsEqualTo(Sid);

        await Assert.That(CarrierHeaderDedupRule.ParseSessionId(legacy)).IsEqualTo(Sid);
    }

    [Test]
    public async Task CarrierPrefixIsBothCarrierAndStub()
    {
        string content = Protocol.CarrierPrefix + "3 separate tool calls. …";
        await Assert.That(RuleHelpers.IsCarrier(content)).IsTrue();
        // Every carrier is also a claudinine stub (the prefixes nest), which is
        // what keeps other rules from re-processing carriers.
        await Assert.That(RuleHelpers.IsClaudinineStub(content)).IsTrue();
    }

    [Test]
    public async Task TrimMarkerCarriesTheSentinel()
    {
        string trimmed = RuleHelpers.HeadTailTrimBytes(new string('x', 5000), 1000);
        await Assert.That(trimmed).Contains(Protocol.TrimSentinel);
        // And a trimmed result is a fixpoint — the sentinel's whole purpose.
        await Assert.That(RuleHelpers.HeadTailTrimBytes(trimmed, 1000)).IsEqualTo(trimmed);
    }

    /// <summary>
    /// The catalog order constraints its comments document, pinned structurally
    /// so a reordering (or an insertion in the wrong place) fails a test
    /// instead of silently changing which content later rules see.
    /// </summary>
    [Test]
    public async Task RuleCatalogOrderContractsHold()
    {
        static int Index<T>() where T : ICompactionRule =>
            Array.FindIndex(RuleCatalog.All, r => r is T);

        // Fork heal must run before ANY rule reads or rewrites digests.
        await Assert.That(Index<ForkHealRule>()).IsEqualTo(0);

        // Header dedup runs immediately after chain-collapse so carriers born
        // this pass are slimmed in the same pass; anchor stubbing follows it.
        await Assert.That(Index<CarrierHeaderDedupRule>())
            .IsEqualTo(Index<ChainCollapseRule>() + 1);
        await Assert.That(Index<AnchorInputStubRule>())
            .IsEqualTo(Index<CarrierHeaderDedupRule>() + 1);

        // Supersession/dedup rules run before age-based ones so a deduped
        // result gets the more informative stub.
        foreach (int dedup in (int[])
            [Index<BashReadDedupRule>(), Index<ReadToolDedupRule>(),
             Index<SystemReminderDedupRule>(), Index<DocumentDedupRule>()])
        {
            await Assert.That(dedup).IsLessThan(Index<ToolResultAgeRule>());
        }

        // Chain-collapse precedes the age tiers (digest previews render from
        // mostly-original content), and the mega-block safety net comes after
        // the age tiers, before the record-removal housekeeping.
        await Assert.That(Index<ChainCollapseRule>()).IsLessThan(Index<ToolResultAgeRule>());
        await Assert.That(Index<ToolResultAgeRule>()).IsLessThan(Index<MegaBlockTrimRule>());
    }
}
