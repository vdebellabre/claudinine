using System.Text.Encodings.Web;
using System.Text.Json;

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
}
