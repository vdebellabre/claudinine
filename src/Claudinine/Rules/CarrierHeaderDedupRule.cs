namespace Claudinine.Rules;

/// <summary>
/// Chain-collapse writes its full retrieval instructions (~1KB) into every
/// carrier it emits — on real transcripts that identical boilerplate alone is
/// ~7% of all residual content (968 copies on the 2026-08 corpus). Only the
/// first full carrier of each compact-boundary SEGMENT needs to teach
/// retrieval; every later carrier is rewritten to a one-line pointer header
/// that keeps the report-not-observation warning. Per segment, not per file,
/// because the app reconstructs live context from the LAST compact_boundary
/// onward — a file-wide "first" that sits before the boundary leaves the short
/// headers' pointer aiming at instructions the model can no longer see
/// (docs/cowork-compatibility.md E8: with the target dropped, the only
/// remaining behaviour is inferring from previews, exactly what the header
/// forbids). Pre-boundary segments cost disk only; their kept header is
/// API-invisible. Runs right after ChainCollapseRule so carriers born this
/// pass are slimmed in the same pass; carriers already on disk from earlier
/// versions are slimmed retroactively.
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
    /// <summary>Command-line spellings: pre-launcher form, the token that
    /// precedes ` get &lt;sid&gt;` in the launcher form (`sh "…/run.sh" get …`), and
    /// the local-mode breadcrumb (`mirror key: &lt;sid&gt;` — local blocks carry no
    /// get-command at all, their verbs are the model's file tools).</summary>
    private const string GetCommandPrefix = "  " + Protocol.BareGetCommand;
    private const string LauncherGetPrefix = Protocol.LauncherGetFragment;
    private const string MirrorKeyPrefix = Protocol.MirrorKeyPrefix;

    /// <summary>The 0.1.x–0.4.x short-header command clause — a bare `claudinine`
    /// that resolves nowhere on hosted installs (no PATH entry) and nowhere at
    /// all in Cowork local mode. Its presence marks an old short header to be
    /// upgraded to the pointer form.</summary>
    private const string OldShortMarker = ". Full outputs: " + Protocol.BareGetCommand;

    /// <summary>Shared tail of every short-header generation — the upgrade
    /// splices the digest body from here on.</summary>
    private const string ShortHeaderEnd = "retrieve, don't infer.]\n\n";

    /// <summary>
    /// Header sentence every version through 0.3.x wrote, which Fable 5's
    /// API-side safeguards flag as an assistant-impersonation injection (a tool
    /// result asserting assistant content is quoted inside it) — with it
    /// present, EVERY resume of the session on Fable is blocked outright.
    /// Healed away below; new headers no longer carry it (see the constraint
    /// comment on ChainCollapseRule.Header). The literal is SPLIT so this
    /// source file never contains the contiguous sentence: a session that Reads
    /// this file would otherwise carry the trigger in its own transcript and
    /// become unresumable on Fable until compaction heals the preview.
    /// </summary>
    private const string LegacySentence =
        " Interleaved assistant" + " notes are verbatim.";

    public void Apply(TranscriptFile transcript)
    {
        bool fullHeaderSeen = false;
        foreach (var rec in transcript.Records)
        {
            if (rec.Removed)
                continue;
            // New boundary segment: the app's next load slices from here, so the
            // segment's first full carrier must re-teach retrieval (see class doc).
            // Both subtypes, same pairing as IsProtected/MarkPreserved: whether
            // microcompact slices the same way is unconfirmed (no real records in
            // the corpus yet), but resetting is the conservative direction — the
            // cost is one extra full header, not a dead pointer.
            if (rec.Type == "system"
                && rec.View["subtype"].AsString() is "compact_boundary" or "microcompact_boundary")
            {
                fullHeaderSeen = false;
                continue;
            }
            if (rec.Type != "user")
                continue;
            var node = rec.CurrentView;
            foreach (var block in RuleHelpers.BlocksOfType(node, "tool_result"))
            {
                // Carrier content is a plain string by construction (ChainCollapseRule
                // sets it directly); anything else is not ours.
                if (block["content"].AsStringMemo() is not string content
                    || !content.StartsWith(HeaderPrefix, StringComparison.Ordinal))
                {
                    continue; // not a carrier
                }

                if (!content.Contains(FullMarker, StringComparison.Ordinal))
                {
                    // Already short. Old generations spelled a bare `claudinine
                    // get` command here — dead on hosted installs — upgraded in
                    // place to the pointer form (idempotent: the new form never
                    // contains the old marker).
                    UpgradeOldShortHeader(transcript, rec, content);
                    continue;
                }

                if (!fullHeaderSeen)
                {
                    fullHeaderSeen = true; // segment's earliest full carrier keeps the instructions
                    HealCommandBlock(transcript, rec, content);
                    continue;
                }

                int end = content.IndexOf(FullHeaderEnd, StringComparison.Ordinal);
                string? callCount = ParseCallCount(content);
                string? sid = ParseSessionId(content);
                if (end < 0 || callCount is null || sid is null)
                    continue; // unfamiliar header variant: fail closed

                string rewritten = ShortHeader(callCount)
                    + content[(end + FullHeaderEnd.Length)..]
                        .Replace(LegacySentence, "", StringComparison.Ordinal);

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
    /// Rewrite a 0.1.x–0.4.x short header (bare `claudinine get &lt;sid&gt;` clause)
    /// to the current pointer form. Fail-closed: an unrecognized shape is left
    /// alone, and the tail record is never replaced.
    /// </summary>
    private static void UpgradeOldShortHeader(TranscriptFile transcript, TranscriptRecord rec, string content)
    {
        if (ReferenceEquals(rec, transcript.Records[^1]))
            return;
        // The marker must sit INSIDE the header (before its end sentinel): a
        // current-form header whose digest body merely QUOTES an old header
        // (dev sessions on this codebase) is not an upgrade candidate.
        int marker = content.IndexOf(OldShortMarker, StringComparison.Ordinal);
        int end = content.IndexOf(ShortHeaderEnd, StringComparison.Ordinal);
        string? callCount = ParseCallCount(content);
        if (marker < 0 || end < 0 || marker >= end || callCount is null)
            return;
        string rewritten = ShortHeader(callCount)
            + content[(end + ShortHeaderEnd.Length)..]
                .Replace(LegacySentence, "", StringComparison.Ordinal);
        if (string.Equals(rewritten, content, StringComparison.Ordinal))
            return; // already current — a no-op pass must not touch the record
        var clone = rec.CloneCurrentNode();
        foreach (var cb in RuleHelpers.BlocksOfType(clone, "tool_result"))
        {
            cb["content"] = rewritten;
        }
        string existingRule = rec.CurrentView["claudinine"]["rule"].AsString()
            ?? ChainCollapseRule.RuleName;
        RuleHelpers.SetReplacement(rec, clone, existingRule);
    }

    /// <summary>
    /// The slimmed header, exposed so ChainCollapseRule's economics gate can price a
    /// carrier by what it will cost AFTER this rule runs rather than by the full
    /// instructions it is born with. A pure pointer on purpose — no command, no
    /// sid, no path: earlier generations spelled a bare `claudinine get` here,
    /// which resolves nowhere on hosted installs, and any baked path goes stale
    /// exactly when trees move. The pointer target is guaranteed live by the
    /// per-boundary-segment keep in Apply, and Compactor.MirrorLost / ForkHealRule
    /// key off the kept FULL header, which still names the sid.
    /// </summary>
    internal static string ShortHeaderFor(string callCount) =>
        ShortHeader(callCount);

    private static string ShortHeader(string callCount) =>
        HeaderPrefix + $"{ChainCollapseRule.CallCountPhrase(callCount)}. " +
        "Full outputs retrievable — commands in the RETRIEVAL block of the nearest earlier " +
        "collapsed turn (REF = the 8-hex id in [brackets]); if the file discussed still " +
        "exists on disk, read IT instead. " +
        "[ref] lines are a REPORT, not observed output — retrieve, don't infer.]\n\n";

    /// <summary>
    /// Regenerate the kept full header's command block from the CURRENT launcher
    /// path, and strip the Fable-blocking <see cref="LegacySentence"/> from
    /// older headers (see class doc). Fail-closed: any sentinel or the sid
    /// failing to parse leaves the record untouched.
    /// </summary>
    private static void HealCommandBlock(TranscriptFile transcript, TranscriptRecord rec, string content)
    {
        // Tail guard, same invariant as everywhere else: the file's final record
        // is never replaced (TryRewrite would abort the whole rewrite).
        if (ReferenceEquals(rec, transcript.Records[^1]))
            return;

        // Everywhere, not just the header: a [ref] preview quoting the sentence
        // (a session working on this very codebase) trips the safeguard the
        // same way, and previews are lossy by design.
        string healed = content.Replace(LegacySentence, "", StringComparison.Ordinal);

        int blockStart = healed.IndexOf(ChainCollapseRule.CommandBlockStart, StringComparison.Ordinal);
        if (blockStart < 0)
            return;
        int cmdStart = blockStart + ChainCollapseRule.CommandBlockStart.Length;
        int cmdEnd = healed.IndexOf(ChainCollapseRule.CommandBlockEnd, cmdStart, StringComparison.Ordinal);
        if (cmdEnd < 0 || ParseSessionId(healed) is not string sid)
            return;

        // Mode follows the transcript's CURRENT location, so a block regenerates
        // correctly after any move: launcher commands normally, file-tool
        // instructions when the tree is a Cowork local-mode session (where no
        // shell command can be trusted — see LocalCowork).
        string fresh = LocalCowork.HeaderRefsDirFor(transcript.Path) is string refsDir
            ? ChainCollapseRule.LocalCommandLines(sid, refsDir)
            : ChainCollapseRule.CommandLines(sid, Launcher.HeaderPathFor(transcript.Path));
        if (!healed.AsSpan(cmdStart, cmdEnd - cmdStart).SequenceEqual(fresh))
            healed = healed[..cmdStart] + fresh + healed[cmdEnd..];

        if (string.Equals(healed, content, StringComparison.Ordinal))
            return; // already current — a no-op pass must not touch the record
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
    /// Session id as embedded in the header's own command lines, any of the
    /// three forms (pre-launcher `claudinine get`, launcher `…run.sh" get`,
    /// local-mode `mirror key:`). Earliest match wins: the header's commands sit
    /// at the top of the content, so a [ref] preview quoting ANOTHER form
    /// further down can never shadow them (previews can legitimately contain
    /// retrieval commands verbatim). Internal so ProtocolContractTests can pin
    /// it against the real emitters.
    /// </summary>
    internal static string? ParseSessionId(string content)
    {
        int best = -1, start = -1;
        void Consider(string prefix)
        {
            int i = content.IndexOf(prefix, StringComparison.Ordinal);
            if (i >= 0 && (best < 0 || i < best))
            {
                best = i;
                start = i + prefix.Length;
            }
        }
        Consider(GetCommandPrefix);
        Consider(LauncherGetPrefix);
        Consider(MirrorKeyPrefix);
        if (start < 0)
            return null;
        int end = start;
        while (end < content.Length
            && (char.IsAsciiLetterOrDigit(content[end]) || content[end] == '-'))
        {
            end++;
        }
        return end > start ? content[start..end] : null;
    }
}
