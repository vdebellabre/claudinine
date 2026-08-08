using System.Text.Json.Nodes;
using Claudinine.Transcript;

namespace Claudinine.Rules;

/// <summary>
/// Removes queue-operation history once it is provably inert: replay every
/// enqueue/dequeue/remove in file order (per sessionId — resumed sessions can
/// interleave) and only if every queue ends EMPTY do the operations go, all of
/// them. A non-empty queue means a message is still pending delivery, and
/// dequeues are positional (they carry no content), so partial removal can never
/// be proven safe under any replay semantics — all-or-nothing is the only sound
/// unit. Anything the replay does not understand fails the whole file closed.
/// </summary>
internal sealed class QueueHistoryCollapseRule : ICompactionRule
{
    public string Name => "queue-history-collapse";

    public void Apply(TranscriptFile transcript)
    {
        var records = transcript.Records;
        // A trailing queue op may be mid-flight at the boundary we run on, and the
        // rewrite layer refuses tail removal anyway. Skip; converges next pass.
        if (records[^1].Type == "queue-operation")
            return;

        var queues = new Dictionary<string, List<string>>();
        var ops = new List<TranscriptRecord>();
        foreach (TranscriptRecord rec in records)
        {
            if (rec.Type != "queue-operation")
                continue;
            if (rec.IsProtected())
                return;
            JsonObject node = rec.Node;
            string sid = node["sessionId"].GetString() ?? "";
            if (!queues.TryGetValue(sid, out List<string>? queue))
                queues[sid] = queue = [];
            string? content = node["content"].GetString();
            switch (node["operation"].GetString())
            {
                case "enqueue" when content is not null:
                    queue.Add(content);
                    break;
                case "dequeue" when queue.Count > 0:
                    queue.RemoveAt(0);
                    break;
                case "remove" when content is not null && queue.Remove(content):
                    break;
                default:
                    return; // unknown op, dequeue on empty, or remove miss
            }
            ops.Add(rec);
        }

        if (ops.Count == 0 || queues.Values.Any(q => q.Count > 0))
            return; // nothing to do, or messages still pending: keep full history

        foreach (TranscriptRecord rec in ops)
            rec.Removed = true;
    }
}
