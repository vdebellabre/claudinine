using Claudinine.Rules;
using Xunit;

// Mirror tests mutate CLAUDE_PLUGIN_DATA (process-wide), so keep runs sequential.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Claudinine.Tests;

public class BashReadParserTests
{
    private static List<ReadTarget> Parse(string cmd) => BashReadParser.ParseReadTargets(cmd);

    [Fact]
    public void SedRange()
    {
        var t = Assert.Single(Parse("sed -n '10,20p' src/foo.cs"));
        Assert.Equal(new ReadTarget("src/foo.cs", 10, 20), t);
    }

    [Fact]
    public void SedRangeUnquoted()
    {
        var t = Assert.Single(Parse("sed -n 10,20p src/foo.cs"));
        Assert.Equal(new ReadTarget("src/foo.cs", 10, 20), t);
    }

    [Fact]
    public void SedSingleLine()
    {
        var t = Assert.Single(Parse("sed -n 42p foo.txt"));
        Assert.Equal(new ReadTarget("foo.txt", 42, 42), t);
    }

    [Fact]
    public void SedInvertedRangeRefused() => Assert.Empty(Parse("sed -n 20,10p foo.txt"));

    [Fact]
    public void SedInPlaceRefused() => Assert.Empty(Parse("sed -i 's/a/b/' foo.txt"));

    [Fact]
    public void CatMultipleFiles()
    {
        var ts = Parse("cat a.txt b.txt");
        Assert.Equal([new("a.txt", 1, null), new("b.txt", 1, null)], ts);
    }

    [Fact]
    public void CatWithFlagRefused() => Assert.Empty(Parse("cat -n a.txt"));

    [Fact]
    public void CatQuotedPathWithSpaces()
    {
        var t = Assert.Single(Parse("cat \"my file.txt\""));
        Assert.Equal("my file.txt", t.Path);
    }

    [Theory]
    [InlineData("head -n 50 f.txt")]
    [InlineData("head -50 f.txt")]
    [InlineData("head --lines 50 f.txt")]
    public void HeadForms(string cmd)
    {
        var t = Assert.Single(Parse(cmd));
        Assert.Equal(new ReadTarget("f.txt", 1, 50), t);
    }

    [Fact]
    public void HeadWithoutCountRefused() => Assert.Empty(Parse("head f.txt"));

    [Fact]
    public void TailRefused() => Assert.Empty(Parse("tail -n 50 f.txt"));

    [Fact]
    public void NonReadVerbRefused() => Assert.Empty(Parse("grep foo f.txt"));

    [Fact]
    public void MixedSegmentPoisonsWholeCommand() => Assert.Empty(Parse("sed -n 1,5p f.txt ; pytest"));

    [Fact]
    public void SemicolonChainOfPureReads()
    {
        var ts = Parse("sed -n '1,5p' a.txt ; sed -n '6,10p' b.txt");
        Assert.Equal(2, ts.Count);
    }

    [Theory]
    [InlineData("cat a.txt | cat b.txt")] // pipe delivers only b — refusing avoids mis-crediting a
    [InlineData("cat a.txt || cat b.txt")]
    [InlineData("cat a.txt && cat b.txt")]
    [InlineData("cat a.txt > out.txt")]
    [InlineData("cat $(pick-file)")]
    [InlineData("cat `pick-file`")]
    [InlineData("cat a.txt &")]
    public void UnsafeShellRefused(string cmd) => Assert.Empty(Parse(cmd));

    [Theory]
    [InlineData("cat 'unclosed")]
    [InlineData("cat trailing\\")]
    [InlineData("")]
    public void UntokenizableRefused(string cmd) => Assert.Empty(Parse(cmd));

    [Fact]
    public void PathPrefixedVerbAccepted()
    {
        var t = Assert.Single(Parse("/usr/bin/sed -n 1,2p f.txt"));
        Assert.Equal(new ReadTarget("f.txt", 1, 2), t);
    }

    [Fact]
    public void SedMultiRange()
    {
        var ts = Parse("sed -n '400,460p;800,860p' src/Service.cs");
        Assert.Equal([new("src/Service.cs", 400, 460), new("src/Service.cs", 800, 860)], ts);
    }

    [Fact]
    public void SedMultiRangeWithNonPrintPartRefused() =>
        Assert.Empty(Parse("sed -n '1,5p;s/a/b/' f.txt"));

    [Fact]
    public void LiteralEchoSeparatorsDoNotPoison()
    {
        var ts = Parse("sed -n '1,10p' a.resx; echo \"=== FR ===\"; sed -n '1,10p' b.resx");
        Assert.Equal([new("a.resx", 1, 10), new("b.resx", 1, 10)], ts);
    }

    [Theory]
    [InlineData("echo hello")]                    // echo alone: nothing read
    [InlineData("echo a; echo b")]
    [InlineData("echo $HOME; cat a.txt")]         // env-dependent echo poisons
    [InlineData("echo -n x; cat a.txt")]          // flags poison
    public void EchoEdgeCasesYieldNoTargets(string cmd) => Assert.Empty(Parse(cmd));

    [Theory]
    [InlineData(1, 100, 10, 20, true)]   // superset covers
    [InlineData(10, 20, 10, 20, true)]   // exact covers
    [InlineData(10, 20, 9, 20, false)]   // starts too late
    [InlineData(10, 20, 10, 21, false)]  // ends too early
    public void CoversRanges(int aStart, int aEnd, int bStart, int bEnd, bool expected) =>
        Assert.Equal(expected, new ReadTarget("f", aStart, aEnd).Covers(new ReadTarget("f", bStart, bEnd)));

    [Fact]
    public void OpenEndedCoversEverythingAtOrAfterStart()
    {
        Assert.True(new ReadTarget("f", 1, null).Covers(new ReadTarget("f", 50, 60)));
        Assert.True(new ReadTarget("f", 1, null).Covers(new ReadTarget("f", 1, null)));
        Assert.False(new ReadTarget("f", 1, 100).Covers(new ReadTarget("f", 50, null)));
        Assert.False(new ReadTarget("f", 1, null).Covers(new ReadTarget("g", 1, 2)));
    }
}
