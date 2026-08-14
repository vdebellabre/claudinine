using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;

namespace Claudinine.Transcript;

internal static class Json
{
    /// <summary>
    /// Options for re-serializing replaced records: compact, minimal escaping so
    /// non-ASCII text survives unmangled (the app writes raw UTF-8 too). JsonNode
    /// serialization is reflection-free, so this is AOT-safe without a context.
    /// </summary>
    public static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The node's string value, or null when absent or not a string. Transcript
    /// shapes are untrusted; GetValue&lt;string&gt;() throws on a wrong-typed field,
    /// which turns one alien record into a silently dead pass — always read
    /// optional strings through this instead.
    /// </summary>
    public static string? GetString(this JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>
    /// <see cref="GetString"/> memoized per node, for PAYLOAD fields (block
    /// text/content): an element-backed JsonValue re-decodes its UTF-8 bytes on
    /// EVERY read (13.7% of pass CPU, eng/bench/profiling-notes.md), and 16 rules
    /// re-read the same payloads. Small-field reads (type, ids) stay on the plain
    /// helper — at their call volume the memo overhead cancels the decode saving.
    /// Reference-keyed caching is sound because a JsonValue's value never changes
    /// — rules replace nodes, never mutate them.
    ///
    /// A plain dictionary cleared by <see cref="ResetMemo"/> at parse time, NOT a
    /// ConditionalWeakTable: the CWT gave the same per-pass lifetime via weak keys
    /// but was a measured top-ten cost (its internal lock on every add, plus
    /// seconds of finalizer-thread time destroying its containers — see the
    /// dotnet-trace entry in eng/bench/profiling-notes.md). Thread-static because
    /// a pass runs on one thread while parallel test classes run many passes.
    /// </summary>
    public static string? GetStringMemo(this JsonNode? node)
    {
        if (node is not JsonValue v)
            return null;
        var memo = DecodedStrings ??= new(ReferenceEqualityComparer.Instance);
        if (memo.TryGetValue(v, out string? cached))
            return cached;
        if (!v.TryGetValue(out string? s))
            return null;
        memo[v] = s;
        return s;
    }

    /// <summary>
    /// <see cref="GetStringMemo(JsonNode?)"/> for the element backing. Elements
    /// are structs with no reference identity, so the memo keys on the RAW UTF-8
    /// value bytes (<see cref="JsonMarshal.GetRawUtf8Value"/> — valid because
    /// records never dispose their documents): equal raw bytes decode to the same
    /// string, so a hit is correct by construction — and identical payloads
    /// repeated across records (re-injected reminders, duplicated file bodies)
    /// now decode ONCE per pass, which the reference-keyed node memo never could.
    /// A miss costs one cheap sample hash + the decode; a hit replaces the decode
    /// (allocate + transcode + validate) with a memcmp.
    /// </summary>
    public static string? GetStringMemo(this JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
            return null;
        var memo = DecodedElementStrings ??= new(RawValueComparer.Instance);
        if (memo.TryGetValue(element, out string? cached))
            return cached;
        string s = element.GetString()!;
        memo[element] = s;
        return s;
    }

    /// <summary>
    /// Drop the previous pass's memo. Called when a transcript is parsed — the
    /// one entry point every pass goes through — so entries never outlive the
    /// tree they cache for. Clear() keeps capacity, so steady reuse allocates
    /// nothing.
    /// </summary>
    internal static void ResetMemo()
    {
        DecodedStrings?.Clear();
        DecodedElementStrings?.Clear();
    }

    [ThreadStatic]
    private static Dictionary<JsonNode, string>? DecodedStrings;

    [ThreadStatic]
    private static Dictionary<JsonElement, string>? DecodedElementStrings;

    /// <summary>
    /// Raw-bytes equality over element VALUES. The hash samples length plus the
    /// first/last 16 bytes — payloads that agree on all three are rare enough
    /// that the memcmp fallback in Equals settles collisions cheaply.
    /// </summary>
    private sealed class RawValueComparer : IEqualityComparer<JsonElement>
    {
        public static readonly RawValueComparer Instance = new();

        public bool Equals(JsonElement x, JsonElement y) =>
            JsonMarshal.GetRawUtf8Value(x).SequenceEqual(JsonMarshal.GetRawUtf8Value(y));

        public int GetHashCode(JsonElement e)
        {
            var span = JsonMarshal.GetRawUtf8Value(e);
            var hash = new HashCode();
            hash.Add(span.Length);
            if (span.Length <= 32)
            {
                hash.AddBytes(span);
            }
            else
            {
                hash.AddBytes(span[..16]);
                hash.AddBytes(span[^16..]);
            }
            return hash.ToHashCode();
        }
    }
}
