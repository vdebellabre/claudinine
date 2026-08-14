namespace Claudinine.Transcript;

/// <summary>
/// One line of a transcript. The raw text is authoritative: untouched records are
/// written back byte-for-byte; only records a rule replaces get re-serialized.
/// </summary>
internal sealed class TranscriptRecord
{
    public required string RawLine { get; init; }

    /// <summary>Parsed view of RawLine. Never mutated — replacements go to <see cref="Replacement"/>.</summary>
    public required JsonObject Node { get; init; }

    /// <summary>Read view of the ORIGINAL parse — what is on disk, ignoring pending edits.</summary>
    public JsonView View => new(Node);

    /// <summary>Read view of the record as the pass currently sees it: pending replacement, else original.</summary>
    public JsonView CurrentView => new(Replacement ?? Node);

    /// <summary>
    /// Mutable deep clone of the record's current state — the ONLY way non-transcript
    /// code obtains a node to mutate (see <see cref="JsonView"/>). The original parse
    /// is never touched; the clone goes back through <see cref="Replacement"/>.
    /// </summary>
    public JsonObject CloneCurrentNode() => (JsonObject)((JsonNode)(Replacement ?? Node)).DeepClone();

    public string? Uuid { get; init; }
    public string? ParentUuid { get; init; }
    public string? Type { get; init; }

    /// <summary>Set by a rule to replace this record on rewrite. Must preserve uuid/parentUuid.</summary>
    public JsonObject? Replacement { get; set; }

    /// <summary>
    /// Set by a rule to drop this record on rewrite. The rewrite layer rechains
    /// surviving children (and leafUuid anchors) to the nearest surviving ancestor.
    /// </summary>
    public bool Removed { get; set; }

    /// <summary>True if the original line ended with a CR (CRLF file) to preserve on rewrite.</summary>
    public required bool HadCarriageReturn { get; init; }

    public static TranscriptRecord? TryParse(string line)
    {
        bool hadCr = line.EndsWith('\r');
        string json = hadCr ? line[..^1] : line;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj)
                return null;
            // Identity fields stay strict: a wrong-typed uuid/parentUuid/type is an
            // unfamiliar shape, and the throw lands in this catch → format sentinel.
            return new TranscriptRecord
            {
                RawLine = line,
                Node = obj,
                Uuid = obj["uuid"]?.GetValue<string>(),
                ParentUuid = obj["parentUuid"]?.GetValue<string>(),
                Type = obj["type"]?.GetValue<string>(),
                HadCarriageReturn = hadCr,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Set at load time when a compact_boundary names this record in
    /// compactMetadata.preservedMessages.allUuids — see TranscriptFile.MarkPreserved.
    /// Those uuids are a THIRD reference class alongside parentUuid and leafUuid:
    /// they are the records the app keeps in context beside the summary after a
    /// boundary, and nothing in the chain points at them, so dangling-parent
    /// validation cannot catch their removal.
    /// </summary>
    public bool IsBoundaryPreserved { get; set; }

    /// <summary>
    /// True if this record must never be removed or structurally modified.
    /// Ported from cozempic's is_protected, minus its in-memory tag keys.
    /// </summary>
    public bool IsProtected()
    {
        // Corpus 2026-08-09 (d8aa7b17): StopHookSummaryStripRule removed a
        // stop_hook_summary that the boundary listed as preserved — a zero-signal
        // record by its own rule's reckoning, but part of the post-boundary context.
        if (IsBoundaryPreserved)
            return true;

        if (Type is "content-replacement" or "marble-origami-commit"
            or "marble-origami-snapshot" or "worktree-state" or "task-summary")
        {
            return true;
        }

        if (Type == "user" && IsTruthy(Node["isCompactSummary"]))
            return true;
        if (Type == "system" &&
            Node["subtype"].GetString() is "compact_boundary" or "microcompact_boundary")
        {
            return true;
        }

        return IsTruthy(Node["isVisibleInTranscriptOnly"]);
    }

    /// <summary>
    /// True when the record belongs to a sidechain (subagent) conversation. In a
    /// MAIN transcript such records are foreign matter spliced into the chain; in
    /// a subagent file (<see cref="TranscriptFile.IsSidechainFile"/>) they are the
    /// conversation itself — guards must read both together.
    /// </summary>
    public bool IsSidechain => IsTruthy(Node["isSidechain"]);

    /// <summary>
    /// A real user turn: message.content is a plain string (tool-result carriers
    /// use a list). DELIBERATELY narrower than RuleHelpers.IsUserPrompt (the age
    /// clock): an image-share user message ticks the clock but must not act as a
    /// chain-collapse turn boundary.
    /// </summary>
    public bool IsRealUserMessage() =>
        Type == "user"
        && !IsTruthy(Node["isCompactSummary"])
        && Node["message"] is JsonObject m
        && m["content"] is JsonValue v
        // Kind check, not TryGetValue<string>: that would decode the entire
        // prompt just to test its type.
        && v.GetValueKind() == JsonValueKind.String;

    private static bool IsTruthy(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue(out bool b) && b;
}
