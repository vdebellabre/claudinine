using System.Text;
using System.Text.Json.Nodes;

namespace Claudinine.Transcript;

/// <summary>
/// A loaded transcript. Load is the format sentinel: any line that is not a JSON
/// object aborts the whole pass (unfamiliar shape → do nothing silently).
/// </summary>
internal sealed class TranscriptFile
{
    public required string Path { get; init; }
    public required List<TranscriptRecord> Records { get; init; }
    public required bool EndsWithNewline { get; init; }

    public static TranscriptFile? TryLoad(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch
        {
            return null;
        }
        if (text.Length == 0)
            return null;

        bool endsWithNewline = text.EndsWith('\n');
        string[] lines = text.Split('\n');
        int count = endsWithNewline ? lines.Length - 1 : lines.Length;

        var records = new List<TranscriptRecord>(count);
        for (int i = 0; i < count; i++)
        {
            if (lines[i].Length == 0)
                return null; // blank interior line: not a shape we know
            TranscriptRecord? rec = TranscriptRecord.TryParse(lines[i]);
            if (rec is null)
                return null; // format sentinel
            records.Add(rec);
        }
        if (records.Count == 0)
            return null;

        return new TranscriptFile { Path = path, Records = records, EndsWithNewline = endsWithNewline };
    }

    public bool HasReplacements => Records.Any(r => r.Replacement is not null);

    /// <summary>
    /// Validate the pending rewrite, then atomically swap it in. Fail-closed: any
    /// validation miss leaves the original file untouched and reports false.
    /// Temp file deliberately does not end in .jsonl (session discovery scans *.jsonl).
    /// </summary>
    public bool TryRewrite()
    {
        if (!HasReplacements)
            return true;

        // Tail-uuid invariant: the app chains the next append off the in-memory
        // tail — the final record must survive byte-for-byte.
        if (Records[^1].Replacement is not null)
            return false;

        var sb = new StringBuilder();
        var rewrittenUuids = new List<string?>(Records.Count);
        for (int i = 0; i < Records.Count; i++)
        {
            TranscriptRecord rec = Records[i];
            string line;
            if (rec.Replacement is JsonObject repl)
            {
                line = repl.ToJsonString(Json.Compact);
                if (rec.HadCarriageReturn) line += "\r";
            }
            else
            {
                line = rec.RawLine;
            }
            sb.Append(line);
            if (i < Records.Count - 1 || EndsWithNewline) sb.Append('\n');
        }
        string rewritten = sb.ToString();

        // Independent re-validation of the full result: every line parses, record
        // count unchanged, uuid/parentUuid sequence identical, tail byte-identical.
        string[] lines = rewritten.Split('\n');
        int count = EndsWithNewline ? lines.Length - 1 : lines.Length;
        if (count != Records.Count)
            return false;
        for (int i = 0; i < count; i++)
        {
            TranscriptRecord? reparsed = TranscriptRecord.TryParse(lines[i]);
            if (reparsed is null)
                return false;
            if (reparsed.Uuid != Records[i].Uuid || reparsed.ParentUuid != Records[i].ParentUuid)
                return false;
        }
        if (lines[count - 1] != Records[^1].RawLine)
            return false;

        string temp = Path + ".claudinine-tmp";
        try
        {
            File.WriteAllText(temp, rewritten, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, Path, overwrite: true);
            return true;
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            return false;
        }
    }
}
