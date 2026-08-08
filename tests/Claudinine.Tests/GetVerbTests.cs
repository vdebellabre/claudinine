using System.Text;
using System.Text.Json.Nodes;
using Claudinine.Mirror;
using Xunit;

namespace Claudinine.Tests;

/// <summary>
/// The `get` command surface — the contract every digest header promises a future
/// model. Exit codes matter as much as output: a silent exit 0 reads as "the
/// output was empty", which is a different claim than "nothing matched".
/// </summary>
public sealed class GetVerbTests : IDisposable
{
    private readonly string _dir;
    private readonly string _mirrorDir;
    private const string Session = "77777777-aaaa-bbbb-cccc-000000000001";
    private const string Uuid1 = "aaaa1111-0000-0000-0000-000000000001";
    private const string Uuid2 = "bbbb2222-0000-0000-0000-000000000002";

    public GetVerbTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "claudinine-tests", Guid.NewGuid().ToString("N"));
        _mirrorDir = Path.Combine(_dir, "plugin-data", "mirrors");
        Directory.CreateDirectory(_mirrorDir);
        Environment.SetEnvironmentVariable("CLAUDE_PLUGIN_DATA", Path.Combine(_dir, "plugin-data"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_PLUGIN_DATA", null);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void WriteMirror(string session, params (string Uuid, string Content)[] results)
    {
        var lines = new List<string>
        {
            new JsonObject
            {
                ["claudinine"] = new JsonObject
                {
                    ["v"] = "1",
                    ["mirrorOf"] = Path.Combine(_dir, session + ".jsonl"),
                },
            }.ToJsonString(),
        };
        foreach ((string uuid, string content) in results)
        {
            lines.Add(new JsonObject
            {
                ["type"] = "user",
                ["uuid"] = uuid,
                ["sessionId"] = session,
                ["message"] = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = "t-" + uuid[..4],
                        ["content"] = content,
                    }),
                },
            }.ToJsonString());
        }
        File.WriteAllText(Path.Combine(_mirrorDir, session + ".jsonl"),
            string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    }

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        TextWriter origOut = Console.Out, origErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try { return (GetVerb.Run(args), stdout.ToString(), stderr.ToString()); }
        finally { Console.SetOut(origOut); Console.SetError(origErr); }
    }

    [Fact]
    public void BareInvocationDefaultsToInfoListing()
    {
        // Previously printed nothing and exited 0 — useless and misleading.
        WriteMirror(Session, (Uuid1, "alpha output\nbeta line"), (Uuid2, "gamma"));

        (int exit, string output, _) = Run(Session);

        Assert.Equal(0, exit);
        Assert.Contains($"[{Uuid1[..8]}]", output);
        Assert.Contains($"[{Uuid2[..8]}]", output);
        Assert.Contains("bytes", output);
        Assert.DoesNotContain("alpha output", output); // listing, not content dump
    }

    [Fact]
    public void GrepPrintsMatchingLinesOnly()
    {
        WriteMirror(Session, (Uuid1, "alpha output\nbeta line"), (Uuid2, "gamma"));

        (int exit, string output, _) = Run(Session, "--grep", "alpha");

        Assert.Equal(0, exit);
        Assert.Contains("alpha output", output);
        Assert.DoesNotContain("beta line", output);
        Assert.DoesNotContain("gamma", output);
    }

    [Fact]
    public void GrepWithNoHitExitsOneWithHint()
    {
        WriteMirror(Session, (Uuid1, "alpha output"));

        (int exit, string output, string err) = Run(Session, "--grep", "zzz-nowhere");

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Contains("no archived output matches", err);
    }

    [Fact]
    public void RefGrepWithNoMatchingLineExitsOneWithHint()
    {
        // Regression: the record exists so the empty-match check passed, the
        // per-line grep printed nothing, and the exit code was 0 — while the
        // same no-hit grep WITHOUT --ref exited 1. The asymmetry was a trap.
        WriteMirror(Session, (Uuid1, "alpha output\nbeta line"));

        (int exit, string output, string err) = Run(Session, "--ref", Uuid1[..8], "--grep", "zzz-nowhere");

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Contains(Uuid1[..8], err);
        Assert.Contains("zzz-nowhere", err);
    }

    [Fact]
    public void RefGrepPrintsMatchingLinesOfThatRecordOnly()
    {
        WriteMirror(Session, (Uuid1, "needle here\nother"), (Uuid2, "needle elsewhere"));

        (int exit, string output, _) = Run(Session, "--ref", Uuid1[..8], "--grep", "needle");

        Assert.Equal(0, exit);
        Assert.Contains("needle here", output);
        Assert.DoesNotContain("needle elsewhere", output);
    }

    [Fact]
    public void RefPrintsFullRecord()
    {
        WriteMirror(Session, (Uuid1, "full record body"));

        (int exit, string output, _) = Run(Session, "--ref", Uuid1[..8]);

        Assert.Equal(0, exit);
        Assert.Contains($"=== [{Uuid1[..8]}] ===", output);
        Assert.Contains("full record body", output);
    }

    [Fact]
    public void UnknownRefExitsOneWithHint()
    {
        WriteMirror(Session, (Uuid1, "content"));

        (int exit, _, string err) = Run(Session, "--ref", "ffffffff");

        Assert.Equal(1, exit);
        Assert.Contains("no archived output for ref", err);
    }

    [Fact]
    public void UnknownSessionExitsOneWithSearchedDirs()
    {
        (int exit, _, string err) = Run("00000000-dead-beef-0000-000000000000");

        Assert.Equal(1, exit);
        Assert.Contains("no mirror found", err);
    }

    [Fact]
    public void UnknownArgumentExitsOne()
    {
        (int exit, _, string err) = Run(Session, "--bogus");

        Assert.Equal(1, exit);
        Assert.Contains("unknown argument", err);
    }

    // ---- ambiguous-prefix resolution: the safety rule had zero assertions ----

    [Fact]
    public void PrefixMatchingTwoSessionsMatchesNothing()
    {
        const string sibling = "77777777-aaaa-bbbb-cccc-000000000002"; // same prefix as Session
        WriteMirror(Session, (Uuid1, "one"));
        WriteMirror(sibling, (Uuid2, "two"));

        Assert.Empty(MirrorFile.FindSessionMirrors("77777777-aaaa"));

        (int exit, _, string err) = Run("77777777-aaaa");
        Assert.Equal(1, exit);
        Assert.Contains("no mirror found", err);
    }

    [Fact]
    public void UniquePrefixResolves()
    {
        WriteMirror(Session, (Uuid1, "unique content"));

        (int exit, string output, _) = Run(Session[..13], "--grep", "unique");

        Assert.Equal(0, exit);
        Assert.Contains("unique content", output);
    }
}
