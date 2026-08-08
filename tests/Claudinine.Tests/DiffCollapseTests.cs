namespace Claudinine.Tests;

public class DiffCollapseTests
{
    private const string RealDiff = """
        diff --git a/foo.cs b/foo.cs
        --- a/foo.cs
        +++ b/foo.cs
        @@ -1,7 +1,7 @@
         using System;
         using System.IO;
         namespace Foo;
        -public class A
        +public class B
         {
             // body
         }
        """;

    [Test]
    public async Task RealUnifiedDiffPassesGate() => await Assert.That(DiffCollapse.LooksLikeUnifiedDiff(RealDiff)).IsTrue();

    [Test]
    public async Task CollapseKeepsChangesAndHeadersDropsContext()
    {
        string collapsed = DiffCollapse.CollapseContext(RealDiff);
        await Assert.That(collapsed).Contains("-public class A");
        await Assert.That(collapsed).Contains("+public class B");
        await Assert.That(collapsed).Contains("@@ -1,7 +1,7 @@");
        await Assert.That(collapsed).Contains("unchanged lines");
        await Assert.That(collapsed).DoesNotContain("using System.IO;");
        await Assert.That(collapsed.Length < RealDiff.Length).IsTrue();
    }

    // The audit cases: a lone coincidental @@-line in non-diff output must never
    // trigger collapse.
    [Test]
    public async Task LoneHunkShapedLineWithoutEnvelopeFailsGate()
    {
        string ciText = "build log\n@@ -1,2 +3,4 @@\n  indented config\n  more config\n";
        await Assert.That(DiffCollapse.LooksLikeUnifiedDiff(ciText)).IsFalse();
    }

    [Test]
    public async Task DecoratedHunkLineInGitLogFragmentFailsGateWithoutEnvelope()
    {
        string gitLog = "commit abc\n\n    some message quoting @@ -1 +1 @@ inline\n";
        await Assert.That(DiffCollapse.LooksLikeUnifiedDiff(gitLog)).IsFalse();
    }

    // Audit P1: indented content AFTER a hunk (git log -p second commit's message
    // body) must be kept verbatim — inHunk resets on the first non-context line.
    [Test]
    public async Task IndentedProseAfterHunkIsKeptVerbatim()
    {
        string gitLogP = string.Join('\n',
            "diff --git a/f b/f",
            "--- a/f",
            "+++ b/f",
            "@@ -1,3 +1,3 @@",
            " ctx1",
            "-old",
            "+new",
            " ctx2",
            "commit def456",
            "    indented commit message line one",
            "    indented commit message line two");
        string collapsed = DiffCollapse.CollapseContext(gitLogP);
        await Assert.That(collapsed).Contains("    indented commit message line one");
        await Assert.That(collapsed).Contains("    indented commit message line two");
    }

    [Test]
    public async Task ReturnsInputWhenCollapsingWouldNotShrink()
    {
        string tiny = "--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n+b";
        await Assert.That(DiffCollapse.CollapseContext(tiny)).IsEqualTo(tiny);
    }
}
