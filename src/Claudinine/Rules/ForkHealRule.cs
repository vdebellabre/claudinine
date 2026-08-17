namespace Claudinine.Rules;

/// <summary>
/// Post-fork mirror heal. The desktop app forks a conversation to a NEW session
/// id (observed 2026-08-08 on an API-error retry): history records are copied
/// with the same uuids, the new id stamped on their sessionId field, and the old
/// jsonl left orphaned. Our digests ride along in that copy — but their retrieval
/// commands still name the PARENT session, and the fork's own mirror never
/// receives the pre-fork originals (mirror-append skips marked records precisely
/// because their original "is already mirrored" — in the parent's mirror). The
/// moment the app ages the orphan out, mirror GC deletes the parent mirror and
/// every pre-fork ref in the LIVE session dies silently.
///
/// The heal: any foreign session id named by a `claudinine get` command inside
/// one of our OWN marked records is a fork-parent CANDIDATE. All three emitters
/// (chain-collapse headers, short headers, anchor-input stubs) spell the id
/// literally, so one textual substitution covers every current and future one.
///
/// Candidates are validated before anything happens, because digests also QUOTE
/// other sessions' retrieval commands (a dev session running `claudinine get X`
/// leaves that command inside its own carrier previews — the 2026-08 corpus is
/// full of these). The discriminator: a record genuinely copied by a fork was
/// mirrored by the parent under the SAME uuid, while a quoting record never
/// appears in the quoted session's mirror. Only a validated parent's mirror is
/// merged into this session's mirror, and only validated records are retargeted
/// — quoted commands stay verbatim, as the historical report they are.
///
/// Fail-closed: no parent mirror found, or no record validating against it →
/// texts stay untouched (old refs keep resolving for as long as the parent
/// mirror lives) and the heal retries every pass. Idempotent by construction:
/// after the retarget no foreign id remains on validated records.
/// </summary>
internal sealed partial class ForkHealRule : ICompactionRule
{
    public string Name => "fork-heal";

    public void Apply(TranscriptFile transcript)
    {
        var records = transcript.Records;
        string currentSid = Path.GetFileNameWithoutExtension(transcript.Path);

        var flagged = new List<(TranscriptRecord Rec, HashSet<string> Sids)>();
        var foreignSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in records)
        {
            if (rec.Removed || rec.IsProtected())
                continue;
            var node = rec.CurrentView;
            if (!node["claudinine"].Exists)
                continue; // only our own rewrites embed retrieval commands
            var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectForeignSids(node, currentSid, sids);
            if (sids.Count == 0)
                continue;
            flagged.Add((rec, sids));
            foreignSids.UnionWith(sids);
        }
        if (flagged.Count == 0)
            return;

        // Validate each candidate parent (fork-vs-quote, see class doc), then
        // mirror-first: a ref is only repointed at this session's mirror once
        // the records it addresses durably live there.
        var healed = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string sid in foreignSids)
        {
            var parentUuids = MirrorFile.MirrorUuidsOf(sid, transcript);
            if (parentUuids is null)
                continue; // no such mirror: stray token, or the parent is gone
            bool genuineFork = flagged.Any(f => f.Sids.Contains(sid)
                && f.Rec.Uuid is not null && parentUuids.Contains(f.Rec.Uuid));
            if (!genuineFork)
                continue; // quoted command, not copied history
            if (MirrorFile.TryAdoptForkParent(sid, transcript))
                healed[sid] = parentUuids;
        }
        if (healed.Count == 0)
            return;

        foreach ((var rec, var sids) in flagged)
        {
            // Tail guard: the file's final record must never be replaced. A fork
            // can end exactly at a marked carrier; it heals on a later pass.
            if (ReferenceEquals(rec, records[^1]))
                continue;
            // Per-record validation again: a post-fork digest quoting the healed
            // parent (uuid born after the fork) keeps its quote verbatim.
            var retarget = sids.Where(sid => healed.TryGetValue(sid, out var parentUuids)
                && rec.Uuid is not null && parentUuids.Contains(rec.Uuid)).ToList();
            if (retarget.Count == 0)
                continue;
            var clone = rec.CloneCurrentNode();
            foreach (string sid in retarget)
                RetargetStrings(clone, sid, currentSid);
            // The marker identifies what the record IS, not who touched it last —
            // same convention as carrier-header-dedup.
            string existingRule = rec.CurrentView["claudinine"]["rule"].AsString() ?? Name;
            RuleHelpers.SetReplacement(rec, clone, existingRule);
        }
    }

    private static void CollectForeignSids(JsonView node, string currentSid, HashSet<string> sids)
    {
        node.ForEachString(text =>
        {
            foreach (Match m in GetCommand().Matches(text))
            {
                string sid = m.Groups[1].Value;
                if (!sid.Equals(currentSid, StringComparison.OrdinalIgnoreCase))
                    sids.Add(sid);
            }
        });
    }

    private static void RetargetStrings(JsonNode? node, string fromSid, string toSid)
    {
        RuleHelpers.VisitStrings(node, text =>
            text.Contains(fromSid, StringComparison.OrdinalIgnoreCase)
                ? text.Replace(fromSid, toSid, StringComparison.OrdinalIgnoreCase)
                : null);
    }

    /// <summary>
    /// A retrieval address's session-id token, in any of the three spellings:
    /// the bare pre-launcher form (`claudinine get &lt;sid&gt;` — old stubs and short
    /// headers, 0.1.x full headers), the launcher form (`sh "…/run.sh" get
    /// &lt;sid&gt;`), or the local-mode breadcrumb (`mirror key: &lt;sid&gt;` — local
    /// headers name no command; their ref FILES are sid-free by design, so the
    /// breadcrumb is what keeps fork adoption discoverable there). Length floor
    /// keeps stray short tokens out; the char class ends the capture at the
    /// first non-sid character. Note the blanket sid replace in RetargetStrings
    /// also fixes the launcher PATH (`…/&lt;sid&gt;/claudinine/run.sh`) in the same pass.
    /// </summary>
    [GeneratedRegex("(?:(?:claudinine|run\\.sh\") get |mirror key: )([A-Za-z0-9][A-Za-z0-9-]{6,})")]
    private static partial Regex GetCommand();
}
