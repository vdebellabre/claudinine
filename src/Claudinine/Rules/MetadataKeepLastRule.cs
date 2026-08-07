using Claudinine.Transcript;

namespace Claudinine.Rules;

/// <summary>
/// Keep-last supersession for uuid-less metadata singletons the app re-derives
/// from the LAST occurrence at load (census 2026-08-07: 407 last-prompt copies
/// across 15 recent transcripts). No uuid means nothing can reference these
/// records, so removal needs no rechaining; the final occurrence of each type is
/// always kept, so the file's tail record is safe by construction.
/// </summary>
internal sealed class MetadataKeepLastRule : ICompactionRule
{
    public string Name => "metadata-keep-last";

    /// <summary>
    /// Types where only the latest record is state. pr-link is excluded (the UI
    /// may list every PR, not just the last) and permission-mode-like records are
    /// covered by keeping the last "mode" — cozempic protected its ancestor
    /// because LOSING the last occurrence breaks resume; keep-last never does.
    /// </summary>
    private static readonly string[] KeepLastTypes = ["last-prompt", "custom-title", "mode"];

    public void Apply(TranscriptFile transcript)
    {
        var records = transcript.Records;
        foreach (string type in KeepLastTypes)
        {
            int last = -1;
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].Type == type)
                    last = i;
            }
            for (int i = 0; i < last; i++)
            {
                if (records[i].Type == type && !records[i].IsProtected())
                    records[i].Removed = true;
            }
        }
    }
}
