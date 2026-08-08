using System.Text.Json.Nodes;
using Claudinine.Transcript;

namespace Claudinine.Rules;

/// <summary>
/// Staleness clock shared by the age-gated rules. Cozempic measured age in user
/// turns only — inert on agentic sessions (measured: 952 records, 12 prompts, 207
/// result carriers → nothing ever aged). A record is aged when EITHER clock says
/// so: user turns since (interactive sessions) or tool results appended since
/// (agentic sessions — the observation-masking unit: the newest N observations are
/// the working set, everything older is maskable).
/// </summary>
internal sealed class AgeIndex
{
    internal const int MidAgeTurns = 15;
    internal const int OldAgeTurns = 40;
    internal const int MidAgeResults = 30;
    internal const int OldAgeResults = 100;

    private readonly int[] _turnsAgo;
    private readonly int[] _resultsAfter;

    public AgeIndex(List<TranscriptRecord> records)
    {
        int totalTurns = 0;
        int[] turnOf = new int[records.Count];
        int totalResults = 0;
        int[] resultsThrough = new int[records.Count];

        for (int i = 0; i < records.Count; i++)
        {
            var node = records[i].Node;
            if (RuleHelpers.IsUserPrompt(node))
                totalTurns++;
            turnOf[i] = totalTurns;

            totalResults += RuleHelpers.ContentBlocks(node).OfType<JsonObject>()
                .Count(b => b["type"].GetString() == "tool_result");
            resultsThrough[i] = totalResults;
        }

        _turnsAgo = new int[records.Count];
        _resultsAfter = new int[records.Count];
        for (int i = 0; i < records.Count; i++)
        {
            _turnsAgo[i] = totalTurns - turnOf[i];
            _resultsAfter[i] = totalResults - resultsThrough[i];
        }
    }

    public bool IsMidAged(int pos) =>
        _turnsAgo[pos] >= MidAgeTurns || _resultsAfter[pos] >= MidAgeResults;

    public bool IsOld(int pos) =>
        _turnsAgo[pos] >= OldAgeTurns || _resultsAfter[pos] >= OldAgeResults;
}
