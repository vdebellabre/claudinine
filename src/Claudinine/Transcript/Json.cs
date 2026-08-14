using System.Runtime.CompilerServices;
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
    /// Drop the previous pass's memo. Called when a transcript is parsed — the
    /// one entry point every pass goes through — so entries never outlive the
    /// tree they cache for. Clear() keeps capacity, so steady reuse allocates
    /// nothing.
    /// </summary>
    internal static void ResetMemo() => DecodedStrings?.Clear();

    [ThreadStatic]
    private static Dictionary<JsonNode, string>? DecodedStrings;
}
