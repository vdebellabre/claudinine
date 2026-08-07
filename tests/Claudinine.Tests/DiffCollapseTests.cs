using Claudinine.Rules;
using Xunit;

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

    [Fact]
    public void RealUnifiedDiffPassesGate() => Assert.True(DiffCollapse.LooksLikeUnifiedDiff(RealDiff));

    [Fact]
    public void CollapseKeepsChangesAndHeadersDropsContext()
    {
        string collapsed = DiffCollapse.CollapseContext(RealDiff);
        Assert.Contains("-public class A", collapsed);
        Assert.Contains("+public class B", collapsed);
        Assert.Contains("@@ -1,7 +1,7 @@", collapsed);
        Assert.Contains("unchanged lines", collapsed);
        Assert.DoesNotContain("using System.IO;", collapsed);
        Assert.True(collapsed.Length < RealDiff.Length);
    }

    // The audit cases: a lone coincidental @@-line in non-diff output must never
    // trigger collapse.
    [Fact]
    public void LoneHunkShapedLineWithoutEnvelopeFailsGate()
    {
        string ciText = "build log\n@@ -1,2 +3,4 @@\n  indented config\n  more config\n";
        Assert.False(DiffCollapse.LooksLikeUnifiedDiff(ciText));
    }

    [Fact]
    public void DecoratedHunkLineInGitLogFragmentFailsGateWithoutEnvelope()
    {
        string gitLog = "commit abc\n\n    some message quoting @@ -1 +1 @@ inline\n";
        Assert.False(DiffCollapse.LooksLikeUnifiedDiff(gitLog));
    }

    // Audit P1: indented content AFTER a hunk (git log -p second commit's message
    // body) must be kept verbatim — inHunk resets on the first non-context line.
    [Fact]
    public void IndentedProseAfterHunkIsKeptVerbatim()
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
        Assert.Contains("    indented commit message line one", collapsed);
        Assert.Contains("    indented commit message line two", collapsed);
    }

    [Fact]
    public void ReturnsInputWhenCollapsingWouldNotShrink()
    {
        string tiny = "--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n+b";
        Assert.Equal(tiny, DiffCollapse.CollapseContext(tiny));
    }
}
