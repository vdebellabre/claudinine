namespace Claudinine.Tests;

/// <summary>
/// Cowork local mode ("On your computer"): hooks and file tools run on the
/// desktop host while the only shell is a Linux microVM that is usually down
/// (docs/cowork-compatibility.md B5–B7), and the colocated mirror sits behind
/// the app's connected-folder allowlist (E6). So inside a
/// `local_&lt;uuid&gt;/.claude/projects/…` tree the digests must teach the model's
/// own Read/Grep tools, pointed at the RefsDump files under `outputs/`,
/// instead of shell commands that resolve to nothing.
/// </summary>
public sealed class LocalCoworkTests : IDisposable
{
    private readonly string _dir;
    private readonly string _root;      // the local_<uuid> session root
    private readonly string _project;   // …/.claude/projects/proj
    private readonly string _refsDir;   // …/outputs/.claudinine/refs
    private static readonly string Output = new('x', 2000);

    public LocalCoworkTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "claudinine-tests", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_dir, "local_" + Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_root, ".claude", "projects", "proj");
        _refsDir = Path.Combine(_root, "outputs", ".claudinine", "refs");
        Directory.CreateDirectory(Path.Combine(_root, "outputs"));
        Directory.CreateDirectory(_project);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string BuildCompactableSession(string? secondMarker = null)
    {
        var b = new TranscriptBuilder().UserPrompt("investigate");
        b.BashRead("sed -n '1,50p' src/a.cs", out _, "payload-one " + Output);
        b.BashRead("sed -n '1,50p' src/b.cs", out _, (secondMarker ?? "payload-two ") + Output);
        b.AssistantText("done");
        return b.WriteTo(_project);
    }

    private static JsonObject[] Load(string path) =>
        File.ReadAllLines(path).Where(l => l.Length > 0)
            .Select(l => (JsonObject)JsonNode.Parse(l)!).ToArray();

    private static string CarrierText(string path) =>
        Load(path).SelectMany(r => (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(x => x["content"])
            .OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out string? s) ? s : "")
            .Single(s => s.StartsWith(ChainCollapseRule.CarrierPrefix, StringComparison.Ordinal));

    [Test]
    public async Task DetectionRequiresTheLocalLayout()
    {
        await Assert.That(LocalCowork.RefsDirFor(Path.Combine(_project, "s.jsonl")))
            .IsEqualTo(_refsDir);
        // An ordinary CLI project tree is not local mode.
        await Assert.That(LocalCowork.RefsDirFor(Path.Combine(_dir, "proj", "s.jsonl"))).IsNull();
        // A local_* ancestor without the outputs/ sibling is not the layout either
        // (and without outputs/ there is nowhere the file tools could read from).
        string bare = Path.Combine(_dir, "local_deadbeef", ".claude", "projects", "p");
        Directory.CreateDirectory(bare);
        await Assert.That(LocalCowork.RefsDirFor(Path.Combine(bare, "s.jsonl"))).IsNull();
    }

    [Test]
    public async Task LocalHeaderTeachesFileToolRetrieval()
    {
        string path = BuildCompactableSession();

        Compactor.Run(path);

        string carrier = CarrierText(path);
        string headerDir = _refsDir.Replace('\\', '/');
        await Assert.That(carrier).Contains("RETRIEVAL — ");
        await Assert.That(carrier).Contains($"DIR = {headerDir}");
        await Assert.That(carrier).Contains("mirror key: test-session");
        await Assert.That(carrier).Contains("Grep DIR/REF.txt");
        await Assert.That(carrier).Contains("REF = the 8-hex id in [brackets]");
        // No shell command anywhere in the block: the session's shell is a
        // foreign (usually absent) VM that cannot run the launcher.
        await Assert.That(carrier).DoesNotContain("sh \"");
        await Assert.That(carrier).DoesNotContain("claudinine get");
    }

    [Test]
    public async Task RefsDumpServesEveryArchivedPayload()
    {
        string path = BuildCompactableSession();

        Compactor.Run(path);

        // Every [ref] the digest emits resolves to a plain text file carrying
        // the full original output — including the collapsed interior call,
        // whose record exists nowhere else but the mirror. (Synthetic uuids all
        // share one 8-hex prefix, so these payloads aggregate into one file —
        // the same multi-match semantics as GetVerb's prefix matching.)
        var contents = Directory.GetFiles(_refsDir, "*.txt").Select(File.ReadAllText).ToList();
        await Assert.That(contents.Any(c => c.Contains("payload-one ", StringComparison.Ordinal))).IsTrue();
        await Assert.That(contents.Any(c => c.Contains("payload-two ", StringComparison.Ordinal))).IsTrue();
        // tool_use records are dumped too (anchor-input stubs address them).
        await Assert.That(contents.Any(c => c.Contains("Bash input: ", StringComparison.Ordinal))).IsTrue();
        // And every ref named by the digest has its file ("ab12cd34" is the
        // header's own REF-binding example, not a ref).
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(CarrierText(path), @"\[([0-9a-f]{8})\]"))
        {
            if (m.Groups[1].Value == "ab12cd34")
                continue;
            await Assert.That(File.Exists(Path.Combine(_refsDir, m.Groups[1].Value + ".txt"))).IsTrue();
        }
    }

    [Test]
    public async Task RefsDumpIsIncrementalAndSelfHealing()
    {
        string path = BuildCompactableSession();
        Compactor.Run(path);
        string aRef = Directory.GetFiles(_refsDir, "*.txt")[0];
        var past = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(aRef, past);

        // Unchanged mirror: the stamp short-circuits the whole dump.
        Compactor.Run(path);
        await Assert.That(File.GetLastWriteTimeUtc(aRef)).IsEqualTo(past);

        // Deleted dump: the next pass regenerates everything from the mirror
        // (the stamp lives inside the dir, so it dies with it).
        Directory.Delete(_refsDir, recursive: true);
        Compactor.Run(path);
        await Assert.That(Directory.GetFiles(_refsDir, "*.txt").Length > 0).IsTrue();
    }

    [Test]
    public async Task LocalAnchorStubStaysAModeFreePointer()
    {
        var b = new TranscriptBuilder().UserPrompt("do the thing");
        b.ToolCall("Bash", new JsonObject { ["command"] = new string('c', 600) }, Output + "0");
        b.ToolCall("Bash", new JsonObject { ["command"] = "echo x" }, Output + "1");
        b.AssistantText("done");
        string path = b.WriteTo(_project);

        Compactor.Run(path);

        // The pointer defers to the carrier's RETRIEVAL block, which in local
        // mode teaches the file tools — so the stub itself needs no path and no
        // command in either mode.
        string text = File.ReadAllText(path);
        await Assert.That(text).Contains("original: ref ");
        await Assert.That(text).Contains("RETRIEVAL block in the nearest collapsed turn");
        await Assert.That(text).DoesNotContain("claudinine get");
    }

    [Test]
    public async Task LocalImageStubNamesTheDumpedMediaFile()
    {
        var b = new TranscriptBuilder().UserPrompt("look at this");
        b.RawImageMessage("m1", new byte[2048]);
        for (int i = 0; i < AgeIndex.MidAgeTurns + 1; i++)
            b.UserPrompt($"turn filler {i}").AssistantText("ok");
        b.AssistantText("done");
        string path = b.WriteTo(_project);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        JsonObject stubbed = records.Single(r =>
            (r["claudinine"] as JsonObject)?["rule"]?.GetValue<string>() == "image-strip");
        string refId = stubbed["uuid"]!.GetValue<string>()[..8];
        string stub = records.SelectMany(r =>
                (r["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(x => x["text"]?.GetValue<string>())
            .Single(t => t?.Contains("-media-") == true)!;
        string headerDir = _refsDir.Replace('\\', '/');
        await Assert.That(stub).Contains($"Glob {headerDir}/{refId}-media-*.*");
        // The dump actually materialized the block the stub promises.
        await Assert.That(File.Exists(Path.Combine(_refsDir, refId + "-media-0.png"))).IsTrue();
    }

    [Test]
    public async Task LostMirrorIsToleratedWhileTheRefsDumpIsIntact()
    {
        string path = BuildCompactableSession();
        Compactor.Run(path);
        Directory.Delete(Path.Combine(_project, "test-session", "claudinine"), recursive: true);

        // Retrieval — the promise the stubs make — is still served by the dump,
        // so the pass continues and rebuilds a fresh mirror going forward.
        Compactor.Run(path);

        await Assert.That(File.Exists(MirrorLocator.PathFor(path))).IsTrue();
    }

    [Test]
    public async Task TripwireStillFiresWhenMirrorAndRefsDumpAreBothGone()
    {
        string path = BuildCompactableSession();
        Compactor.Run(path);
        string compacted = File.ReadAllText(path);
        Directory.Delete(Path.Combine(_project, "test-session", "claudinine"), recursive: true);
        Directory.Delete(_refsDir, recursive: true);

        Compactor.Run(path);

        await Assert.That(File.ReadAllText(path)).IsEqualTo(compacted);
        await Assert.That(File.Exists(MirrorLocator.PathFor(path))).IsFalse();
    }

    [Test]
    public async Task ForkHealRetargetsTheMirrorKey()
    {
        string parentPath = BuildCompactableSession();
        Compactor.Run(parentPath);
        await Assert.That(File.ReadAllText(parentPath)).Contains("mirror key: test-session");

        string forkPath = Path.Combine(_project, "fork-session.jsonl");
        var lines = File.ReadAllLines(parentPath).Select(l =>
        {
            var node = (JsonObject)JsonNode.Parse(l)!;
            if (node["sessionId"] is not null)
                node["sessionId"] = "fork-session";
            return node.ToJsonString();
        });
        File.WriteAllText(forkPath, string.Join("\n", lines) + "\n", new UTF8Encoding(false));

        Compactor.Run(forkPath);

        string text = File.ReadAllText(forkPath);
        await Assert.That(text).Contains("mirror key: fork-session");
        await Assert.That(text).DoesNotContain("mirror key: test-session");
    }

    [Test]
    public async Task LocalPassIsIdempotent()
    {
        string path = BuildCompactableSession();
        Compactor.Run(path);
        string afterFirst = File.ReadAllText(path);

        Compactor.Run(path);

        await Assert.That(File.ReadAllText(path)).IsEqualTo(afterFirst);
    }
}
