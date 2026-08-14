using System.Diagnostics;
using System.Globalization;
using System.Text;
using Claudinine.Rules;
using Claudinine.Transcript;

// Pass-only timing harness: parse + rules + compute-rewrite, N times, in THIS
// process. No mirror, no file writes, no hook machinery — the same span the
// bench `profile` verb measures, but runnable as JIT (`dotnet run`) or Native
// AOT (`dotnet publish -r win-x64`) to separate codegen and fresh-process
// effects from everything else. See "the 13x multiplier that wasn't" in
// eng/bench/profiling-notes.md for what it settled.
//
// args: <transcript> [iterations]
// stdout: one line of per-iteration ms, space-separated. Iteration 1 in a
// fresh process shows the cold-heap (and, for JIT, first-compile) cost; the
// tail shows the warmed pass.

string path = Path.GetFullPath(args[0]);
int n = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 12;
string text = File.ReadAllText(path);

var times = new List<double>(n);
long check = 0;
for (int i = 0; i < n; i++)
{
    var sw = Stopwatch.StartNew();
    var transcript = TranscriptFile.TryParseText(text, path, Encoding.UTF8.GetByteCount(text));
    if (transcript is null)
    {
        Console.Error.WriteLine("parse failed");
        return 1;
    }
    foreach (var rule in RuleCatalog.All)
        rule.Apply(transcript);
    var lines = transcript.TryComputeRewrite();
    sw.Stop();
    check += lines?.Count ?? -1;
    times.Add(sw.Elapsed.TotalMilliseconds);
}

Console.WriteLine(string.Join(' ', times.Select(t => t.ToString("F2", CultureInfo.InvariantCulture))));
Console.Error.WriteLine($"check={check}");
return 0;
