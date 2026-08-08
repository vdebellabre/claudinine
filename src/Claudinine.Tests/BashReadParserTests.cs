// Mirror tests mutate CLAUDE_PLUGIN_DATA (process-wide), so keep runs sequential.
[assembly: NotInParallel]

namespace Claudinine.Tests;

public class BashReadParserTests
{
    private static List<ReadTarget> Parse(string cmd) => BashReadParser.ParseReadTargets(cmd);

    [Test]
    public async Task SedRange()
    {
        var t = await Assert.That(Parse("sed -n '10,20p' src/foo.cs")).HasSingleItem();
        await Assert.That(t).IsEqualTo(new ReadTarget("src/foo.cs", 10, 20));
    }

    [Test]
    public async Task SedRangeUnquoted()
    {
        var t = await Assert.That(Parse("sed -n 10,20p src/foo.cs")).HasSingleItem();
        await Assert.That(t).IsEqualTo(new ReadTarget("src/foo.cs", 10, 20));
    }

    [Test]
    public async Task SedSingleLine()
    {
        var t = await Assert.That(Parse("sed -n 42p foo.txt")).HasSingleItem();
        await Assert.That(t).IsEqualTo(new ReadTarget("foo.txt", 42, 42));
    }

    [Test]
    public async Task SedInvertedRangeRefused() => await Assert.That(Parse("sed -n 20,10p foo.txt")).IsEmpty();

    [Test]
    public async Task SedInPlaceRefused() => await Assert.That(Parse("sed -i 's/a/b/' foo.txt")).IsEmpty();

    [Test]
    public async Task CatMultipleFiles()
    {
        var ts = Parse("cat a.txt b.txt");
        await Assert.That(ts).IsEquivalentTo(
            [new ReadTarget("a.txt", 1, null), new ReadTarget("b.txt", 1, null)]);
    }

    [Test]
    public async Task CatWithFlagRefused() => await Assert.That(Parse("cat -n a.txt")).IsEmpty();

    [Test]
    public async Task CatQuotedPathWithSpaces()
    {
        var t = await Assert.That(Parse("cat \"my file.txt\"")).HasSingleItem();
        await Assert.That(t.Path).IsEqualTo("my file.txt");
    }
    [Test]
    [Arguments("head -n 50 f.txt")]
    [Arguments("head -50 f.txt")]
    [Arguments("head --lines 50 f.txt")]
    public async Task HeadForms(string cmd)
    {
        var t = await Assert.That(Parse(cmd)).HasSingleItem();
        await Assert.That(t).IsEqualTo(new ReadTarget("f.txt", 1, 50));
    }

    [Test]
    public async Task HeadWithoutCountRefused() => await Assert.That(Parse("head f.txt")).IsEmpty();

    [Test]
    public async Task TailRefused() => await Assert.That(Parse("tail -n 50 f.txt")).IsEmpty();

    [Test]
    public async Task NonReadVerbRefused() => await Assert.That(Parse("grep foo f.txt")).IsEmpty();

    [Test]
    public async Task MixedSegmentPoisonsWholeCommand() => await Assert.That(Parse("sed -n 1,5p f.txt ; pytest")).IsEmpty();

    [Test]
    public async Task SemicolonChainOfPureReads()
    {
        var ts = Parse("sed -n '1,5p' a.txt ; sed -n '6,10p' b.txt");
        await Assert.That(ts.Count).IsEqualTo(2);
    }
    [Test]
    [Arguments("cat a.txt | cat b.txt")] // pipe delivers only b — refusing avoids mis-crediting a
    [Arguments("cat a.txt || cat b.txt")]
    [Arguments("cat a.txt && cat b.txt")]
    [Arguments("cat a.txt > out.txt")]
    [Arguments("cat $(pick-file)")]
    [Arguments("cat `pick-file`")]
    [Arguments("cat a.txt &")]
    public async Task UnsafeShellRefused(string cmd) => await Assert.That(Parse(cmd)).IsEmpty();
    [Test]
    [Arguments("cat 'unclosed")]
    [Arguments("cat trailing\\")]
    [Arguments("")]
    public async Task UntokenizableRefused(string cmd) => await Assert.That(Parse(cmd)).IsEmpty();

    [Test]
    public async Task PathPrefixedVerbAccepted()
    {
        var t = await Assert.That(Parse("/usr/bin/sed -n 1,2p f.txt")).HasSingleItem();
        await Assert.That(t).IsEqualTo(new ReadTarget("f.txt", 1, 2));
    }

    [Test]
    public async Task SedMultiRange()
    {
        var ts = Parse("sed -n '400,460p;800,860p' src/Service.cs");
        await Assert.That(ts).IsEquivalentTo(
            [new ReadTarget("src/Service.cs", 400, 460), new ReadTarget("src/Service.cs", 800, 860)]);
    }

    [Test]
    public async Task SedMultiRangeWithNonPrintPartRefused() =>
        await Assert.That(Parse("sed -n '1,5p;s/a/b/' f.txt")).IsEmpty();

    [Test]
    public async Task LiteralEchoSeparatorsDoNotPoison()
    {
        var ts = Parse("sed -n '1,10p' a.resx; echo \"=== FR ===\"; sed -n '1,10p' b.resx");
        await Assert.That(ts).IsEquivalentTo(
            [new ReadTarget("a.resx", 1, 10), new ReadTarget("b.resx", 1, 10)]);
    }
    [Test]
    [Arguments("echo hello")]                    // echo alone: nothing read
    [Arguments("echo a; echo b")]
    [Arguments("echo $HOME; cat a.txt")]         // env-dependent echo poisons
    [Arguments("echo -n x; cat a.txt")]          // flags poison
    public async Task EchoEdgeCasesYieldNoTargets(string cmd) => await Assert.That(Parse(cmd)).IsEmpty();
    [Test]
    [Arguments(1, 100, 10, 20, true)]   // superset covers
    [Arguments(10, 20, 10, 20, true)]   // exact covers
    [Arguments(10, 20, 9, 20, false)]   // starts too late
    [Arguments(10, 20, 10, 21, false)]  // ends too early
    public async Task CoversRanges(int aStart, int aEnd, int bStart, int bEnd, bool expected) =>
        await Assert.That(new ReadTarget("f", aStart, aEnd).Covers(new ReadTarget("f", bStart, bEnd))).IsEqualTo(expected);

    [Test]
    public async Task OpenEndedCoversEverythingAtOrAfterStart()
    {
        await Assert.That(new ReadTarget("f", 1, null).Covers(new ReadTarget("f", 50, 60))).IsTrue();
        await Assert.That(new ReadTarget("f", 1, null).Covers(new ReadTarget("f", 1, null))).IsTrue();
        await Assert.That(new ReadTarget("f", 1, 100).Covers(new ReadTarget("f", 50, null))).IsFalse();
        await Assert.That(new ReadTarget("f", 1, null).Covers(new ReadTarget("g", 1, 2))).IsFalse();
    }
}
