using System.Text;
using System.Text.Json.Nodes;
using Claudinine.Mirror;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

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

    /// <summary>
    /// The verb writes straight to Console; TUnit already redirects it per test,
    /// so we read its capture instead of swapping the writer ourselves. That
    /// capture accumulates across a test, hence the slice from the pre-call
    /// length: each Run returns only what that invocation printed.
    /// </summary>
    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        TestContext ctx = TestContext.Current!;
        int before = ctx.GetStandardOutput().Length, beforeErr = ctx.GetErrorOutput().Length;
        int exit = GetVerb.Run(args);
        return (exit,
            ctx.GetStandardOutput()[before..],
            ctx.GetErrorOutput()[beforeErr..]);
    }

    [Test]
    public async Task BareInvocationDefaultsToInfoListing()
    {
        // Previously printed nothing and exited 0 — useless and misleading.
        WriteMirror(Session, (Uuid1, "alpha output\nbeta line"), (Uuid2, "gamma"));

        (int exit, string output, _) = Run(Session);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains($"[{Uuid1[..8]}]");
        await Assert.That(output).Contains($"[{Uuid2[..8]}]");
        await Assert.That(output).Contains("bytes");
        await Assert.That(output).DoesNotContain("alpha output"); // listing, not content dump
    }

    [Test]
    public async Task GrepPrintsMatchingLinesOnly()
    {
        WriteMirror(Session, (Uuid1, "alpha output\nbeta line"), (Uuid2, "gamma"));

        (int exit, string output, _) = Run(Session, "--grep", "alpha");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("alpha output");
        await Assert.That(output).DoesNotContain("beta line");
        await Assert.That(output).DoesNotContain("gamma");
    }

    [Test]
    public async Task GrepWithNoHitExitsOneWithHint()
    {
        WriteMirror(Session, (Uuid1, "alpha output"));

        (int exit, string output, string err) = Run(Session, "--grep", "zzz-nowhere");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(output).IsEqualTo("");
        await Assert.That(err).Contains("no archived output matches");
    }

    [Test]
    public async Task RefGrepWithNoMatchingLineExitsOneWithHint()
    {
        // Regression: the record exists so the empty-match check passed, the
        // per-line grep printed nothing, and the exit code was 0 — while the
        // same no-hit grep WITHOUT --ref exited 1. The asymmetry was a trap.
        WriteMirror(Session, (Uuid1, "alpha output\nbeta line"));

        (int exit, string output, string err) = Run(Session, "--ref", Uuid1[..8], "--grep", "zzz-nowhere");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(output).IsEqualTo("");
        await Assert.That(err).Contains(Uuid1[..8]);
        await Assert.That(err).Contains("zzz-nowhere");
    }

    [Test]
    public async Task RefGrepPrintsMatchingLinesOfThatRecordOnly()
    {
        WriteMirror(Session, (Uuid1, "needle here\nother"), (Uuid2, "needle elsewhere"));

        (int exit, string output, _) = Run(Session, "--ref", Uuid1[..8], "--grep", "needle");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("needle here");
        await Assert.That(output).DoesNotContain("needle elsewhere");
    }

    [Test]
    public async Task RefPrintsFullRecord()
    {
        WriteMirror(Session, (Uuid1, "full record body"));

        (int exit, string output, _) = Run(Session, "--ref", Uuid1[..8]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains($"=== [{Uuid1[..8]}] ===");
        await Assert.That(output).Contains("full record body");
    }

    [Test]
    public async Task UnknownRefExitsOneWithHint()
    {
        WriteMirror(Session, (Uuid1, "content"));

        (int exit, _, string err) = Run(Session, "--ref", "ffffffff");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(err).Contains("no archived output for ref");
    }

    [Test]
    public async Task UnknownSessionExitsOneWithSearchedDirs()
    {
        (int exit, _, string err) = Run("00000000-dead-beef-0000-000000000000");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(err).Contains("no mirror found");
    }

    [Test]
    public async Task UnknownArgumentExitsOne()
    {
        (int exit, _, string err) = Run(Session, "--bogus");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(err).Contains("unknown argument");
    }

    // ---- ambiguous-prefix resolution: the safety rule had zero assertions ----

    [Test]
    public async Task PrefixMatchingTwoSessionsMatchesNothing()
    {
        const string sibling = "77777777-aaaa-bbbb-cccc-000000000002"; // same prefix as Session
        WriteMirror(Session, (Uuid1, "one"));
        WriteMirror(sibling, (Uuid2, "two"));

        await Assert.That(MirrorLocator.FindSessionMirrors("77777777-aaaa")).IsEmpty();

        (int exit, _, string err) = Run("77777777-aaaa");
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(err).Contains("no mirror found");
    }

    [Test]
    public async Task UniquePrefixResolves()
    {
        WriteMirror(Session, (Uuid1, "unique content"));

        (int exit, string output, _) = Run(Session[..13], "--grep", "unique");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("unique content");
    }
}
