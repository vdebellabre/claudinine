namespace Claudinine.Rules;

/// <summary>
/// Chain-collapse writes its full retrieval instructions (~1KB) into every
/// carrier it emits — on real transcripts that identical boilerplate alone is
/// ~7% of all residual content (968 copies on the 2026-08 corpus). Only the
/// file's FIRST full carrier needs to teach retrieval; every later carrier is
/// rewritten to a one-line header that keeps the essential command syntax and
/// the report-not-observation warning. Runs right after ChainCollapseRule so
/// carriers born this pass are slimmed in the same pass; carriers already on
/// disk from earlier versions are slimmed retroactively.
///
/// The rewrite preserves the carrier's existing marker rule name: the marker
/// identifies what the record IS (a chain-collapse carrier, which is how
/// ChainCollapseRule recognizes its own output), not who touched it last.
///
/// This rule is also where the kept header SELF-HEALS: the launcher path its
/// command lines embed is absolute, and goes stale exactly when the tree moves
/// (cloud↔local, home rename, project re-slug) — which is when the colocated
/// mirror moved WITH the transcript and retrieval should still work. On every
/// pass the first full carrier's command block is regenerated from the current
/// launcher path; a pre-launcher (0.1.x/0.2.x, bare `claudinine get`) block is
/// upgraded by the same rewrite. Idempotent — a block already current is left
/// byte-identical — and no other byte of the record is disturbed.
/// </summary>
internal sealed class CarrierHeaderDedupRule : ICompactionRule
{
    public string Name => "carrier-header-dedup";

    private const string HeaderPrefix = ChainCollapseRule.CarrierPrefix;
    /// <summary>Present only in the full-instructions header, never in the short one.</summary>
    private const string FullMarker = "\nRETRIEVAL — ";
    private const string FullHeaderEnd = "do not infer it from the preview.]\n\n";
    /// <summary>Command-line spellings: pre-launcher form, and the token that
    /// precedes ` get &lt;sid&gt;` in the launcher form (`sh "…/run.sh" get …`).</summary>
    private const string GetCommandPrefix = "  claudinine get ";
    private const string LauncherGetPrefix = "\" get ";

    public void Apply(TranscriptFile transcript)
    {
        bool fullHeaderSeen = false;
        foreach (var rec in transcript.Records)
        {
            if (rec.Removed || rec.Type != "user")
                continue;
            var node = rec.CurrentView;
            foreach (var block in RuleHelpers.BlocksOfType(node, "tool_result"))
            {
                // Carrier content is a plain string by construction (ChainCollapseRule
                // sets it directly); anything else is not ours.
                if (block["content"].AsStringMemo() is not string content
                    || !content.StartsWith(HeaderPrefix, StringComparison.Ordinal)
                    || !content.Contains(FullMarker, StringComparison.Ordinal))
                {
                    continue; // short already (idempotence) or not a carrier
                }

                if (!fullHeaderSeen)
                {
                    fullHeaderSeen = true; // earliest full carrier keeps the instructions
                    HealCommandBlock(transcript, rec, content);
                    continue;
                }

                int end = content.IndexOf(FullHeaderEnd, StringComparison.Ordinal);
                string? callCount = ParseCallCount(content);
                string? sid = ParseSessionId(content);
                if (end < 0 || callCount is null || sid is null)
                    continue; // unfamiliar header variant: fail closed

                string rewritten = ShortHeader(callCount, sid) + content[(end + FullHeaderEnd.Length)..];

                var clone = rec.CloneCurrentNode();
                foreach (var cb in RuleHelpers.BlocksOfType(clone, "tool_result"))
                {
                    cb["content"] = rewritten;
                }
                string existingRule = node["claudinine"]["rule"].AsString()
                    ?? ChainCollapseRule.RuleName;
                RuleHelpers.SetReplacement(rec, clone, existingRule);
            }
        }
    }

    /// <summary>
    /// The slimmed header, exposed so ChainCollapseRule's economics gate can price a
    /// carrier by what it will cost AFTER this rule runs (see
    /// ChainCollapseRule.HeaderDedupSavingBytes) rather than by the full instructions
    /// it is born with.
    /// </summary>
    internal static string ShortHeaderFor(string callCount, string sid) =>
        ShortHeader(callCount, sid);

    private static string ShortHeader(string callCount, string sid) =>
        HeaderPrefix + $"{ChainCollapseRule.CallCountPhrase(callCount)}. " +
        $"Full outputs: claudinine get {sid} --ref REF [--grep PATTERN | --info | --full | --media] " +
        "(full retrieval guidance in the first collapsed block of this session; if the file " +
        "discussed still exists on disk, read IT instead). " +
        "[ref] lines are a REPORT, not observed output — retrieve, don't infer.]\n\n";

    /// <summary>
    /// Regenerate the kept full header's command block from the CURRENT launcher
    /// path (see class doc). Fail-closed: any sentinel or the sid failing to
    /// parse leaves the record untouched.
    /// </summary>
    private static void HealCommandBlock(TranscriptFile transcript, TranscriptRecord rec, string content)
    {
        // Tail guard, same invariant as everywhere else: the file's final record
        // is never replaced (TryRewrite would abort the whole rewrite).
        if (ReferenceEquals(rec, transcript.Records[^1]))
            return;

        int blockStart = content.IndexOf(ChainCollapseRule.CommandBlockStart, StringComparison.Ordinal);
        if (blockStart < 0)
            return;
        int cmdStart = blockStart + ChainCollapseRule.CommandBlockStart.Length;
        int cmdEnd = content.IndexOf(ChainCollapseRule.CommandBlockEnd, cmdStart, StringComparison.Ordinal);
        if (cmdEnd < 0 || ParseSessionId(content) is not string sid)
            return;

        string fresh = ChainCollapseRule.CommandLines(sid, Launcher.HeaderPathFor(transcript.Path));
        if (content.AsSpan(cmdStart, cmdEnd - cmdStart).SequenceEqual(fresh))
            return; // already current — a no-op pass must not touch the record

        string healed = content[..cmdStart] + fresh + content[cmdEnd..];
        var clone = rec.CloneCurrentNode();
        foreach (var cb in RuleHelpers.BlocksOfType(clone, "tool_result"))
        {
            cb["content"] = healed;
        }
        string existingRule = rec.CurrentView["claudinine"]["rule"].AsString()
            ?? ChainCollapseRule.RuleName;
        RuleHelpers.SetReplacement(rec, clone, existingRule);
    }

    /// <summary>
    /// Digits immediately after the header prefix ("…originally ran 12 separate…", or
    /// "…originally ran 1 tool call" for a single-call carrier — both start with the
    /// digits, so this parse covers the singular form too).
    /// </summary>
    private static string? ParseCallCount(string content)
    {
        int start = HeaderPrefix.Length, end = start;
        while (end < content.Length && char.IsAsciiDigit(content[end]))
            end++;
        return end > start ? content[start..end] : null;
    }

    /// <summary>
    /// Session id as embedded in the header's own command lines, either form.
    /// Earliest match wins: the header's commands sit at the top of the content,
    /// so a [ref] preview quoting the OTHER form further down can never shadow
    /// them (previews can legitimately contain retrieval commands verbatim).
    /// </summary>
    private static string? ParseSessionId(string content)
    {
        int iOld = content.IndexOf(GetCommandPrefix, StringComparison.Ordinal);
        int iNew = content.IndexOf(LauncherGetPrefix, StringComparison.Ordinal);
        int start;
        if (iOld >= 0 && (iNew < 0 || iOld < iNew))
            start = iOld + GetCommandPrefix.Length;
        else if (iNew >= 0)
            start = iNew + LauncherGetPrefix.Length;
        else
            return null;
        int end = content.IndexOf(' ', start);
        return end > start ? content[start..end] : null;
    }
}
