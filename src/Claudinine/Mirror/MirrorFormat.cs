namespace Claudinine.Mirror;

/// <summary>
/// The claudinine bookkeeping envelope shared by mirror headers, fork separators
/// and skip markers: <c>{"claudinine":{"v":"1","&lt;field&gt;":"&lt;value&gt;"}}</c>.
/// </summary>
internal static class MirrorFormat
{
    public const string Version = "1";

    public static string Line(string field, string value) =>
        new JsonObject
        {
            ["claudinine"] = new JsonObject { ["v"] = Version, [field] = value },
        }.ToJsonString(Json.Compact);
}
