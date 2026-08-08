namespace Claudinine.Rules;

/// <summary>
/// Read-tool sibling of <see cref="BashReadDedupRule"/>: a Read result superseded
/// by a later Read covering the same line range of the same file_path. This is
/// where the real volume is for Read-tool-heavy sessions (most of them).
/// </summary>
internal sealed class ReadToolDedupRule : ReadSupersessionRule
{
    /// <summary>
    /// A Read without an explicit limit returns at most this many lines (documented
    /// app default). Its coverage claim must stop there — treating it as read-to-EOF
    /// would let a truncated read wrongly supersede an earlier deep-offset read.
    /// </summary>
    internal const int DefaultReadLimit = 2000;

    public override string Name => "read-dedup";

    protected internal override bool IsReadTool(string toolName) => toolName is "Read" or "read";

    protected internal override List<ReadTarget> ExtractTargets(JsonObject toolUseBlock)
    {
        var none = new List<ReadTarget>();
        if (toolUseBlock["input"] is not JsonObject input)
            return none;
        if (input["file_path"] is not JsonValue pathValue
            || !pathValue.TryGetValue(out string? path) || path.Length == 0)
        {
            return none;
        }
        // Page-ranged PDF reads etc. aren't line-addressed; refuse anything with
        // input fields we don't fully understand beyond the three known ones.
        foreach (var (key, _) in input)
        {
            if (key is not ("file_path" or "offset" or "limit"))
                return none;
        }

        int start = 1;
        if (input["offset"] is JsonValue ov)
        {
            if (!ov.TryGetValue(out int offset) || offset < 0)
                return none;
            // The app treats offset as the 1-based start line; 0 behaves as 1.
            start = Math.Max(offset, 1);
        }

        int limit = DefaultReadLimit;
        if (input["limit"] is JsonValue lv)
        {
            if (!lv.TryGetValue(out int explicitLimit) || explicitLimit <= 0)
                return none;
            limit = explicitLimit;
        }

        return [new ReadTarget(path, start, start + limit - 1)];
    }
}
