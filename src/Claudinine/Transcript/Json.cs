using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        node is JsonValue v && v.TryGetValue<string>(out string? s) ? s : null;
}
