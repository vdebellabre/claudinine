using System.Buffers;
using System.Text.Encodings.Web;

namespace Claudinine.Transcript;

/// <summary>
/// Read-only view over a record's parsed JSON — the ONE seam through which all
/// non-transcript-layer code (rules, helpers, mirror) reads record trees. Nothing
/// outside the Transcript layer may touch node or element kinds for reading;
/// mutation happens only on <see cref="JsonObject"/> CLONES obtained via
/// <see cref="TranscriptRecord.CloneCurrentNode"/>, which stay JsonObject forever.
///
/// Two backings, exactly one set per instance: a <see cref="JsonElement"/> for
/// original parses (allocation-free reads over the line's UTF-8 — the read-layer
/// refactor, eng/bench/profiling-notes.md), or a <see cref="JsonNode"/> for
/// pending Replacement clones and mirror-line parses. <c>default(JsonView)</c> is
/// the undefined view: a default element has ValueKind.Undefined and every
/// accessor already answers "absent" for it.
///
/// Semantics mirror JsonNode's, which the rules were written against and the
/// corpus A/B pins: an explicit JSON null reads as ABSENT (<see cref="Exists"/>
/// false — JsonNode surfaces null as a null reference), and all reads are
/// never-throw on untrusted shapes (a throw would kill the whole pass).
/// </summary>
internal readonly struct JsonView
{
    private readonly JsonNode? _node;
    private readonly JsonElement _element;

    public JsonView(JsonNode? node) => _node = node;

    public JsonView(JsonElement element) => _element = element;

    /// <summary>Present and not JSON null.</summary>
    public bool Exists => _node is not null
        || _element.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);

    public bool IsObject =>
        _node is not null ? _node is JsonObject : _element.ValueKind == JsonValueKind.Object;

    public bool IsArray =>
        _node is not null ? _node is JsonArray : _element.ValueKind == JsonValueKind.Array;

    /// <summary>A leaf value (string/number/bool) — the old `is JsonValue` tests.</summary>
    public bool IsValue => _node is not null
        ? _node is JsonValue
        : _element.ValueKind is JsonValueKind.String or JsonValueKind.Number
            or JsonValueKind.True or JsonValueKind.False;

    /// <summary>Kind check without decoding (a payload string can be huge).</summary>
    public bool IsString => _node is not null
        ? _node is JsonValue v && v.GetValueKind() == JsonValueKind.String
        : _element.ValueKind == JsonValueKind.String;

    /// <summary>Property access; undefined when absent or this is not an object.</summary>
    public JsonView this[string key]
    {
        get
        {
            if (_node is not null)
                return _node is JsonObject o ? new(o[key]) : default;
            return _element.ValueKind == JsonValueKind.Object
                && _element.TryGetProperty(key, out var e) ? new(e) : default;
        }
    }

    /// <summary>Array element; undefined when out of range or this is not an array.</summary>
    public JsonView this[int index]
    {
        get
        {
            if (_node is not null)
                return _node is JsonArray a && (uint)index < (uint)a.Count ? new(a[index]) : default;
            return _element.ValueKind == JsonValueKind.Array
                && (uint)index < (uint)_element.GetArrayLength() ? new(_element[index]) : default;
        }
    }

    /// <summary>
    /// Key present at all, even with a null value — stricter than
    /// <c>this[key].Exists</c>, which reads an explicit null as absent. The
    /// distinction is the old ContainsKey idempotence checks.
    /// </summary>
    public bool HasProperty(string key) => _node is not null
        ? _node is JsonObject o && o.ContainsKey(key)
        : _element.ValueKind == JsonValueKind.Object && _element.TryGetProperty(key, out _);

    /// <summary>String value, or null when absent or not a string (never throws).</summary>
    public string? AsString() => _node is not null
        ? _node.GetString()
        : _element.ValueKind == JsonValueKind.String ? _element.GetString() : null;

    /// <summary>
    /// <see cref="AsString"/> for PAYLOAD fields, memoized per pass on both
    /// backings — per node reference on the node side, per raw UTF-8 value on the
    /// element side (see the two Json.GetStringMemo overloads). Small-field reads
    /// (type, ids) stay on <see cref="AsString"/>: at their call volume the memo
    /// overhead cancels the decode saving (measured, eng/bench/profiling-notes.md).
    /// </summary>
    public string? AsStringMemo() =>
        _node is not null ? _node.GetStringMemo() : _element.GetStringMemo();

    /// <summary>Int value, or null when absent or not an int.</summary>
    public int? AsInt() => _node is not null
        ? _node is JsonValue v && v.TryGetValue(out int i) ? i : null
        : _element.ValueKind == JsonValueKind.Number && _element.TryGetInt32(out int e) ? e : null;

    /// <summary>True iff the value is boolean true (the old IsTruthy helpers).</summary>
    public bool IsTrue => _node is not null
        ? _node is JsonValue v && v.TryGetValue(out bool b) && b
        : _element.ValueKind == JsonValueKind.True;

    /// <summary>Array length; 0 when not an array.</summary>
    public int Count => _node is not null
        ? _node is JsonArray a ? a.Count : 0
        : _element.ValueKind == JsonValueKind.Array ? _element.GetArrayLength() : 0;

    /// <summary>Array items in order; empty when not an array.</summary>
    public IEnumerable<JsonView> Items
    {
        get
        {
            if (_node is not null)
            {
                if (_node is not JsonArray a)
                    yield break;
                foreach (var item in a)
                    yield return new(item);
            }
            else if (_element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in _element.EnumerateArray())
                    yield return new(item);
            }
        }
    }

    /// <summary>Object properties in document order; empty when not an object.</summary>
    public IEnumerable<(string Key, JsonView Value)> Properties
    {
        get
        {
            if (_node is not null)
            {
                if (_node is not JsonObject o)
                    yield break;
                foreach (var kv in o)
                    yield return (kv.Key, new(kv.Value));
            }
            else if (_element.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in _element.EnumerateObject())
                    yield return (p.Name, new(p.Value));
            }
        }
    }

    /// <summary>
    /// Visit every string leaf, read-only (the collect half of the old
    /// RuleHelpers.VisitStrings; the mutating half stays node-typed, clones only).
    /// Recursion depth is bounded by the parser (document depth caps at 64).
    /// </summary>
    public void ForEachString(Action<string> visit)
    {
        if (_node is not null)
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
            return;
        }

        switch (_element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in _element.EnumerateObject())
                    new JsonView(p.Value).ForEachString(visit);
                break;
            case JsonValueKind.Array:
                foreach (var item in _element.EnumerateArray())
                    new JsonView(item).ForEachString(visit);
                break;
            case JsonValueKind.String:
                visit(_element.GetString()!);
                break;
        }
    }

    /// <summary>
    /// Serialized size heuristic (anchor-input-stub's threshold), default
    /// serializer options both ways. The default encoder escapes all non-ASCII,
    /// so the element side's UTF-8 byte count equals the node side's
    /// <c>ToJsonString().Length</c> char count. Caller guarantees <see cref="Exists"/>.
    /// </summary>
    public int SerializedLength()
    {
        if (_node is not null)
            return _node.ToJsonString().Length;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            _element.WriteTo(writer);
        return buffer.WrittenCount;
    }

    /// <summary>Compact re-serialization (tool-result minify), Json.Compact escaping both ways. Caller guarantees <see cref="Exists"/>.</summary>
    public string ToCompactJson()
    {
        if (_node is not null)
            return _node.ToJsonString(Json.Compact);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, RelaxedWriter))
            _element.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Writer twin of <see cref="Json.Compact"/> — same escaping, no indent.</summary>
    private static readonly JsonWriterOptions RelaxedWriter = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Parse free text (tool-result payloads, not record trees). Undefined when
    /// structurally invalid — and also when the text is the literal `null`, which
    /// parses to ValueKind.Null; both mean "nothing to read", same as before.
    /// Only JsonException is a parse failure; anything else propagates, as it did
    /// at the old call sites. The returned view keeps its JsonDocument alive via
    /// the element's internal reference; nothing to dispose.
    /// </summary>
    public static JsonView TryParse(string text)
    {
        try
        {
            return new(JsonDocument.Parse(text).RootElement);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
