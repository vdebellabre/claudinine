namespace Claudinine.Transcript;

/// <summary>
/// One line of a transcript. The raw text is authoritative: untouched records are
/// written back byte-for-byte; only records a rule replaces get re-serialized.
/// </summary>
internal sealed class TranscriptRecord
{
    public required string RawLine { get; init; }

    /// <summary>
    /// Root element of RawLine's parse — read-only by construction. The element
    /// keeps its JsonDocument alive through an internal reference; the document
    /// is never disposed (per-invocation process, and a live element must never
    /// outlive its document — GC reclaims both with the record). Replacements go
    /// to <see cref="Replacement"/> as mutable node trees.
    /// </summary>
    public required JsonElement Root { get; init; }

    /// <summary>Read view of the ORIGINAL parse — what is on disk, ignoring pending edits.</summary>
    public JsonView View => new(Root);

    /// <summary>Read view of the record as the pass currently sees it: pending replacement, else original.</summary>
    public JsonView CurrentView => Replacement is not null ? new(Replacement) : new(Root);

    /// <summary>
    /// Mutable clone of the record's current state — the ONLY way non-transcript
    /// code obtains a node to mutate (see <see cref="JsonView"/>). The original
    /// parse is never touched; the clone goes back through <see cref="Replacement"/>.
    /// The element side needs no deep clone: an element-backed JsonObject
    /// materializes copies on mutation, and the immutable element is untouched.
    /// </summary>
    public JsonObject CloneCurrentNode() => Replacement is not null
        ? (JsonObject)Replacement.DeepClone()
        : JsonObject.Create(Root)!; // Root is always an object (TryParse checked)

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
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                return null;
            }
            return new TranscriptRecord
            {
                RawLine = line,
                Root = root,
                Uuid = IdentityString(root, "uuid"),
                ParentUuid = IdentityString(root, "parentUuid"),
                Type = IdentityString(root, "type"),
                HadCarriageReturn = hadCr,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Identity fields stay strict: GetString() throws on a wrong-typed
    /// uuid/parentUuid/type — an unfamiliar shape — and the throw lands in
    /// TryParse's catch → format sentinel, exactly like GetValue&lt;string&gt; did
    /// on the node graph. Absent or explicit null reads as null.
    /// </summary>
    private static string? IdentityString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null)
            return null;
        return e.GetString();
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

        if (Type == "user" && View["isCompactSummary"].IsTrue)
            return true;
        if (Type == "system" &&
            View["subtype"].AsString() is "compact_boundary" or "microcompact_boundary")
        {
            return true;
        }

        return View["isVisibleInTranscriptOnly"].IsTrue;
    }

    /// <summary>
    /// True when the record belongs to a sidechain (subagent) conversation. In a
    /// MAIN transcript such records are foreign matter spliced into the chain; in
    /// a subagent file (<see cref="TranscriptFile.IsSidechainFile"/>) they are the
    /// conversation itself — guards must read both together.
    /// </summary>
    public bool IsSidechain => View["isSidechain"].IsTrue;

    /// <summary>
    /// A real user turn: message.content is a plain string (tool-result carriers
    /// use a list). DELIBERATELY narrower than RuleHelpers.IsUserPrompt (the age
    /// clock): an image-share user message ticks the clock but must not act as a
    /// chain-collapse turn boundary. IsString is a kind check — it never decodes
    /// the prompt just to test its type.
    /// </summary>
    public bool IsRealUserMessage() =>
        Type == "user"
        && !View["isCompactSummary"].IsTrue
        && View["message"]["content"].IsString;
}
