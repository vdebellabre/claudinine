using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Claudinine.Transcript;

namespace Claudinine.Mirror;

/// <summary>
/// Per-session uncompacted mirror: what the transcript would contain if we never
/// compacted. Append-only, idempotent by uuid — its tail IS the progress marker,
/// so steady-state appends, crash recovery and SessionEnd all share one algorithm.
/// Serves restore, retrieval (stubs carry origUuid) and savings measurement.
/// </summary>
internal static class MirrorFile
{
    private const string HeaderVersion = "1";

    public static string MirrorsDirectory()
    {
        string root = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA")
            ?? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claudinine");
        return System.IO.Path.Combine(root, "mirrors");
    }

    public static string PathFor(string transcriptPath) =>
        System.IO.Path.Combine(
            MirrorsDirectory(),
            System.IO.Path.GetFileNameWithoutExtension(transcriptPath) + ".jsonl");

    /// <summary>
    /// Append every transcript record not yet mirrored. Must succeed BEFORE any
    /// compaction (mirror-first invariant): nothing is ever stubbed that is not
    /// already mirrored. Records that already carry a claudinine marker are skipped —
    /// their original went into the mirror when they were first seen.
    /// </summary>
    public static bool TryAppendMissing(TranscriptFile transcript)
    {
        try
        {
            string mirrorPath = PathFor(transcript.Path);
            Directory.CreateDirectory(MirrorsDirectory());

            var seen = new HashSet<string>();
            bool hasHeader = false;
            if (File.Exists(mirrorPath))
            {
                foreach (string line in File.ReadLines(mirrorPath, Encoding.UTF8))
                {
                    if (line.Length == 0) continue;
                    if (!hasHeader) { hasHeader = true; continue; }
                    seen.Add(IdentityOf(line));
                }
            }

            var toAppend = new List<string>();
            if (!hasHeader)
            {
                var header = new JsonObject
                {
                    ["claudinine"] = new JsonObject
                    {
                        ["v"] = HeaderVersion,
                        ["mirrorOf"] = System.IO.Path.GetFullPath(transcript.Path),
                    },
                };
                toAppend.Add(header.ToJsonString(Json.Compact));
            }
            foreach (TranscriptRecord rec in transcript.Records)
            {
                if (rec.Node["claudinine"] is not null)
                    continue; // already a stub; its original is already mirrored
                string line = rec.HadCarriageReturn ? rec.RawLine[..^1] : rec.RawLine;
                if (seen.Add(IdentityOf(line, rec.Uuid)))
                    toAppend.Add(line);
            }

            if (toAppend.Count == 0)
                return true;

            using var stream = new FileStream(mirrorPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            foreach (string line in toAppend)
                writer.Write(line + "\n");
            writer.Flush();
            stream.Flush(flushToDisk: true); // the invariant is only real once it's durable
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Delete mirrors whose transcript no longer exists (the app ages transcripts
    /// out itself). Run at SessionStart; failures are ignored record by record.
    /// </summary>
    public static void CollectGarbage()
    {
        string dir = MirrorsDirectory();
        if (!Directory.Exists(dir))
            return;
        foreach (string mirror in Directory.EnumerateFiles(dir, "*.jsonl"))
        {
            try
            {
                string? headerLine = File.ReadLines(mirror, Encoding.UTF8).FirstOrDefault();
                if (headerLine is null) continue;
                if (JsonNode.Parse(headerLine) is not JsonObject header) continue;
                string? mirrorOf = header["claudinine"]?["mirrorOf"]?.GetValue<string>();
                if (mirrorOf is not null && !File.Exists(mirrorOf))
                    File.Delete(mirror);
            }
            catch
            {
                // Unreadable mirror: leave it for a human.
            }
        }
    }

    /// <summary>Identity for the "already mirrored?" test: uuid when present, else a content hash.</summary>
    private static string IdentityOf(string line, string? knownUuid = null)
    {
        string? uuid = knownUuid;
        if (uuid is null)
        {
            try
            {
                uuid = (JsonNode.Parse(line) as JsonObject)?["uuid"]?.GetValue<string>();
            }
            catch
            {
                // fall through to hash
            }
        }
        if (uuid is not null)
            return "u:" + uuid;
        return "h:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line)));
    }
}
