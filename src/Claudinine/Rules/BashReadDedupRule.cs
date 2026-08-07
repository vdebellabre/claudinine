using System.Text.Json.Nodes;

namespace Claudinine.Rules;

/// <summary>
/// Collapse Bash file-read results (`sed -n`/`cat`/`head`) superseded by a later
/// read of the same range. Port of cozempic's POC: such a result is a reproducible
/// slice of a file still on disk — once a later read covers the same range, the
/// earlier copy carries nothing the later one lacks. Parsing is fail-closed; see
/// <see cref="BashReadParser"/>.
/// </summary>
internal sealed class BashReadDedupRule : ReadSupersessionRule
{
    public override string Name => "bash-read-dedup";

    protected internal override bool IsReadTool(string toolName) => toolName is "Bash" or "bash";

    protected internal override List<ReadTarget> ExtractTargets(JsonObject toolUseBlock)
    {
        string? cmd = (toolUseBlock["input"] as JsonObject)?["command"]?.GetValue<string>();
        return BashReadParser.ParseReadTargets(cmd);
    }
}
