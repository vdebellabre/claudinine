namespace Claudinine;

/// <summary>
/// THE string protocol: every phrase whose exact spelling more than one class
/// depends on lives here, so a wording change is a one-file diff instead of a
/// silent matcher outage. Correctness genuinely hinges on these literals —
/// emitters write them into transcripts and matchers recognize our own output
/// by them, across version generations that are already on disk forever.
///
/// The emitter/matcher matrix (ProtocolContractTests pins it):
///
///   Fragment                    Emitted by                       Matched by
///   --------------------------  -------------------------------  ----------------------------------------
///   <see cref="StubPrefix"/>    every stub and carrier           RuleHelpers.IsClaudinineStub
///   <see cref="CarrierPrefix"/> ChainCollapseRule.Header         RuleHelpers.IsCarrier, CarrierHeaderDedupRule
///   <see cref="TrimSentinel"/>  RuleHelpers.HeadTailTrimBytes    ToolResultAgeRule, MegaBlockTrimRule
///   <see cref="BareGetCommand"/> 0.1.x–0.4.x stubs + headers     Compactor.MirrorLost, ForkHealRule.GetCommand,
///                               (legacy, still on disk)          CarrierHeaderDedupRule.ParseSessionId, CloneVerb
///   <see cref="LauncherGetFragment"/> ChainCollapseRule.CommandLines, Compactor.MirrorLost (JSON-escaped — see
///                               ImageStripRule launcher stubs    <see cref="LauncherGetFragmentJsonEscaped"/>),
///                                                                ForkHealRule, ParseSessionId, CloneVerb
///   <see cref="MirrorKeyPrefix"/> ChainCollapseRule.LocalCommandLines  Compactor.MirrorLost, ForkHealRule,
///                                                                ParseSessionId, CloneVerb
///
/// The forms-forever rule: transcripts compacted by every past version stay on
/// disk, so a matcher may gain forms but must NEVER drop one — the bare
/// `claudinine get` (0.1.x–0.4.x), the launcher `" get` and the local-mode
/// `mirror key:` are all live in the wild. ForkHealRule's regex cannot be
/// composed from these constants (attribute literal), so the contract test is
/// what keeps it in agreement.
/// </summary>
internal static class Protocol
{
    /// <summary>
    /// Opens every piece of content this tool writes into a transcript — stubs,
    /// carriers, trim markers. RuleHelpers.IsClaudinineStub keys on it so no
    /// rule ever re-processes our own output.
    /// </summary>
    public const string StubPrefix = "[claudinine";

    /// <summary>
    /// The literal opening of every chain-collapse carrier. Carrier-header
    /// dedup and anchor-input stubbing recognize carriers ONLY by this exact
    /// prefix — any header must be built from this constant, never respelled
    /// (a wording tweak here would silently disable both downstream rules).
    /// </summary>
    public const string CarrierPrefix = "[claudinine: this turn originally ran ";

    /// <summary>
    /// Fixpoint sentinel present in every head/tail-trim marker: content
    /// carrying it is our own trim output and must never be re-trimmed
    /// (multibyte content can trim to just over a byte cap — each pass would
    /// then shave a sliver off the previous pass's tail).
    /// </summary>
    public const string TrimSentinel = "trimmed by claudinine]";

    /// <summary>
    /// The pre-launcher retrieval command, `claudinine get &lt;sid&gt;`. Current
    /// emitters no longer write it (hosted installs have no PATH entry), but
    /// 0.1.x–0.4.x transcripts carry it forever, so every matcher keeps it.
    /// </summary>
    public const string BareGetCommand = "claudinine get ";

    /// <summary>
    /// The launcher-form fragment immediately preceding the sid:
    /// `sh "…/run.sh" get &lt;sid&gt;` — the closing quote of the quoted launcher
    /// path plus ` get `. Matching this fragment (rather than the path) is what
    /// keeps recognition stable across moved trees.
    /// </summary>
    public const string LauncherGetFragment = "\" get ";

    /// <summary>
    /// <see cref="LauncherGetFragment"/> as it appears on a RAW jsonl line: the
    /// quote is JSON-escaped there (`…run.sh\" get &lt;sid&gt;`). Compactor.MirrorLost
    /// scans RawLine, so matching the unescaped form would never hit.
    /// </summary>
    public const string LauncherGetFragmentJsonEscaped = "\\" + LauncherGetFragment;

    /// <summary>
    /// The local-mode (Cowork "On your computer") breadcrumb, `mirror key:
    /// &lt;sid&gt;` — local blocks carry no get-command at all (their verbs are the
    /// model's file tools), so this clause is the only place the sid rides.
    /// </summary>
    public const string MirrorKeyPrefix = "mirror key: ";
}
