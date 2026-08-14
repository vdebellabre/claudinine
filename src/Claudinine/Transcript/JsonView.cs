namespace Claudinine.Transcript;

/// <summary>
/// Read-only view over a record's parsed JSON — the ONE seam through which all
/// non-transcript-layer code (rules, helpers, mirror) reads record trees. Nothing
/// outside the Transcript layer may touch <see cref="JsonNode"/> kinds for
/// reading; mutation happens only on <see cref="JsonObject"/> CLONES obtained via
/// <see cref="TranscriptRecord.CloneCurrentNode"/>, which stay JsonObject forever.
/// This split is what makes the planned backing swap (JsonNode graph →
/// JsonDocument/JsonElement, see eng/bench/profiling-notes.md "read-layer
/// refactor") a change to THIS file and the transcript layer only.
///
/// Semantics deliberately mirror JsonNode's so the port is behavior-identical:
/// JsonNode surfaces an explicit JSON null as a null reference, so
/// <see cref="Exists"/> is false for BOTH a missing key and an explicit null —
/// exactly what every `is not null` test in the rules used to mean. All reads are
/// never-throw on untrusted shapes (a throw would kill the whole pass).
/// </summary>
internal readonly struct JsonView
{
    private readonly JsonNode? _node;

    public JsonView(JsonNode? node) => _node = node;

    /// <summary>Present and not JSON null (JsonNode's own null-reference convention).</summary>
    public bool Exists => _node is not null;

    public bool IsObject => _node is JsonObject;
    public bool IsArray => _node is JsonArray;

    /// <summary>A leaf value (string/number/bool) — the old `is JsonValue` tests.</summary>
    public bool IsValue => _node is JsonValue;

    /// <summary>Kind check without decoding (a payload string can be huge).</summary>
    public bool IsString => _node is JsonValue v && v.GetValueKind() == JsonValueKind.String;

    /// <summary>Property access; undefined when absent or this is not an object.</summary>
    public JsonView this[string key] => _node is JsonObject o ? new(o[key]) : default;

    /// <summary>Array element; undefined when out of range or this is not an array.</summary>
    public JsonView this[int index] =>
        _node is JsonArray a && (uint)index < (uint)a.Count ? new(a[index]) : default;

    /// <summary>
    /// Key present at all, even with a null value — stricter than
    /// <c>this[key].Exists</c>, which reads an explicit null as absent. The
    /// distinction is the old ContainsKey idempotence checks.
    /// </summary>
    public bool HasProperty(string key) => _node is JsonObject o && o.ContainsKey(key);

    /// <summary>String value, or null when absent or not a string (never throws).</summary>
    public string? AsString() => _node.GetString();

    /// <summary><see cref="AsString"/> memoized per node — PAYLOAD fields only (see Json.GetStringMemo).</summary>
    public string? AsStringMemo() => _node.GetStringMemo();

    /// <summary>Int value, or null when absent or not an int.</summary>
    public int? AsInt() => _node is JsonValue v && v.TryGetValue(out int i) ? i : null;

    /// <summary>True iff the value is boolean true (the old IsTruthy helpers).</summary>
    public bool IsTrue => _node is JsonValue v && v.TryGetValue(out bool b) && b;

    /// <summary>Array length; 0 when not an array.</summary>
    public int Count => _node is JsonArray a ? a.Count : 0;

    /// <summary>Array items in order; empty when not an array.</summary>
    public IEnumerable<JsonView> Items
    {
        get
        {
            if (_node is not JsonArray a)
                yield break;
            foreach (var item in a)
                yield return new(item);
        }
    }

    /// <summary>Object properties in document order; empty when not an object.</summary>
    public IEnumerable<(string Key, JsonView Value)> Properties
    {
        get
        {
            if (_node is not JsonObject o)
                yield break;
            foreach (var kv in o)
                yield return (kv.Key, new(kv.Value));
        }
    }

    /// <summary>
    /// Visit every string leaf, read-only (the collect half of the old
    /// RuleHelpers.VisitStrings; the mutating half stays node-typed, clones only).
    /// Recursion depth is bounded by the parser (document depth caps at 64).
    /// </summary>
    public void ForEachString(Action<string> visit)
    {
        switch (_node)
        {
            case JsonObject obj:
                foreach (var kv in obj)
                    new JsonView(kv.Value).ForEachString(visit);
                break;
            case JsonArray array:
                foreach (var item in array)
                    new JsonView(item).ForEachString(visit);
                break;
            case JsonValue value when value.TryGetValue(out string? text):
                visit(text);
                break;
        }
    }

    /// <summary>
    /// Serialized size heuristic (anchor-input-stub's threshold). Default
    /// serializer options, matching the old <c>input.ToJsonString().Length</c>.
    /// Caller guarantees <see cref="Exists"/>.
    /// </summary>
    public int SerializedLength() => _node!.ToJsonString().Length;

    /// <summary>Compact re-serialization (tool-result minify). Caller guarantees <see cref="Exists"/>.</summary>
    public string ToCompactJson() => _node!.ToJsonString(Json.Compact);

    /// <summary>
    /// Parse free text (tool-result payloads, not record trees). Undefined when
    /// structurally invalid — and also when the text is the literal `null`, which
    /// parses to a null node; both mean "nothing to read", same as before.
    /// Only JsonException is a parse failure; anything else propagates, as it did
    /// at the old call sites.
    /// </summary>
    public static JsonView TryParse(string text)
    {
        try
        {
            return new(JsonNode.Parse(text));
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
