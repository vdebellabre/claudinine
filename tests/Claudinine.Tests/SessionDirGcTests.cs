using Xunit;

namespace Claudinine.Tests;

public sealed class SessionDirGcTests : IDisposable
{
    private readonly string _dir;
    private readonly string _transcriptPath;
    private const string CurrentSessionId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    public SessionDirGcTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "claudinine-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _transcriptPath = Path.Combine(_dir, CurrentSessionId + ".jsonl");
        File.WriteAllText(_transcriptPath, "{}\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string CreateSessionDir(string name, bool old = true)
    {
        string dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(dir, "tool-results"));
        File.WriteAllText(Path.Combine(dir, "tool-results", "abc123.txt"), "persisted output");
        if (old)
            AgeTree(dir);
        return dir;
    }

    private static void AgeTree(string dir)
    {
        DateTime old = DateTime.UtcNow - TimeSpan.FromDays(30);
        foreach (string entry in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(entry))
            {
                Directory.SetCreationTimeUtc(entry, old);
                Directory.SetLastWriteTimeUtc(entry, old);
            }
            else
            {
                File.SetCreationTimeUtc(entry, old);
                File.SetLastWriteTimeUtc(entry, old);
            }
        }
        Directory.SetCreationTimeUtc(dir, old);
        Directory.SetLastWriteTimeUtc(dir, old);
    }

    [Fact]
    public void DeletesOldOrphanSessionDir()
    {
        string orphan = CreateSessionDir("11111111-2222-3333-4444-555555555555");

        SessionDirGc.Run(_transcriptPath, CurrentSessionId);

        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void KeepsSessionDirWhoseTranscriptExists()
    {
        string alive = CreateSessionDir("11111111-2222-3333-4444-555555555555");
        File.WriteAllText(Path.Combine(_dir, "11111111-2222-3333-4444-555555555555.jsonl"), "{}\n");

        SessionDirGc.Run(_transcriptPath, CurrentSessionId);

        Assert.True(Directory.Exists(alive));
    }

    [Fact]
    public void KeepsNonUuidDirectories()
    {
        // The project dir also holds user data — memory/ and its backups must
        // never match, no matter how old or transcript-less.
        string memory = Path.Combine(_dir, "memory");
        Directory.CreateDirectory(memory);
        File.WriteAllText(Path.Combine(memory, "MEMORY.md"), "# index\n");
        AgeTree(memory);
        string backup = Path.Combine(_dir, "memory.backup-20260530");
        Directory.CreateDirectory(backup);
        AgeTree(backup);

        SessionDirGc.Run(_transcriptPath, CurrentSessionId);

        Assert.True(Directory.Exists(memory));
        Assert.True(File.Exists(Path.Combine(memory, "MEMORY.md")));
        Assert.True(Directory.Exists(backup));
    }

    [Fact]
    public void KeepsNearUuidNames()
    {
        // Uppercase hex, wrong length, wrong dash positions: all non-matches.
        foreach (string name in new[]
        {
            "11111111-2222-3333-4444-55555555555", // 35 chars
            "11111111-2222-3333-4444-5555555555556", // 37 chars
            "11111111-2222-3333-4444-55555555555G", // non-hex
            "11111111-2222-3333-4444-55555555555A".ToUpperInvariant(),
            "111111112222-3333-4444-5555555555556", // dash misplaced
        })
        {
            string dir = Path.Combine(_dir, name);
            Directory.CreateDirectory(dir);
            AgeTree(dir);
        }

        SessionDirGc.Run(_transcriptPath, CurrentSessionId);

        Assert.Equal(5, Directory.EnumerateDirectories(_dir).Count());
    }

    [Fact]
    public void KeepsFreshOrphanDir()
    {
        // Grace window: a dir touched recently may belong to a session still
        // materializing on disk.
        string fresh = CreateSessionDir("11111111-2222-3333-4444-555555555555", old: false);

        SessionDirGc.Run(_transcriptPath, CurrentSessionId);

        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void GraceConsidersNestedFiles()
    {
        // The session dir's own mtime does not change when files land deep in
        // subagents/ — a recent nested file must still hold the whole dir.
        string orphan = CreateSessionDir("11111111-2222-3333-4444-555555555555");
        string nested = Path.Combine(orphan, "subagents", "agent-1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(nested, "{}\n");
        Directory.SetLastWriteTimeUtc(Path.Combine(orphan, "subagents"),
            DateTime.UtcNow - TimeSpan.FromDays(30));
        Directory.SetLastWriteTimeUtc(orphan, DateTime.UtcNow - TimeSpan.FromDays(30));

        SessionDirGc.Run(_transcriptPath, CurrentSessionId);

        Assert.True(Directory.Exists(orphan));
    }

    [Fact]
    public void NeverDeletesCurrentSessionDir()
    {
        // Belt and braces: even orphan-shaped, old, and transcript-less, the
        // running session's own dir is out of bounds.
        File.Delete(_transcriptPath);
        string own = CreateSessionDir(CurrentSessionId);

        SessionDirGc.Run(_transcriptPath, CurrentSessionId);

        Assert.True(Directory.Exists(own));
    }

    [Fact]
    public void SurvivesMissingProjectDir()
    {
        SessionDirGc.Run(Path.Combine(_dir, "nope", "missing.jsonl"), CurrentSessionId);
    }
}
