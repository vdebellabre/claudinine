using Claudinine.Rules;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Claudinine.Tests;

/// <summary>
/// The digest preview heuristics — pure functions, heuristic-heavy, and the only
/// thing a future session sees without paying for retrieval. Each test pins the
/// verdict-over-head ordering the class doc promises.
/// </summary>
public class PreviewRendererTests
{
    [Test]
    public async Task PytestSummaryWinsOverHead()
    {
        string text = "collected 12 items\n"
            + "test_a.py::test_one PASSED\n"
            + "FAILED test_b.py::test_two - AssertionError\n"
            + "==== 1 failed, 11 passed in 3.21s ====\n";

        string preview = PreviewRenderer.RenderPreview("Bash", "python -m pytest", text);

        await Assert.That(preview).Contains("RESULT: 1 failed, 11 passed");
        await Assert.That(preview).Contains("first failures: test_b.py::test_two");
    }

    [Test]
    public async Task ErrorMarkerLineSurfacedNotHead()
    {
        string text = "step one ok\nstep two ok\nerror: everything broke\ntail line";

        string preview = PreviewRenderer.RenderPreview("Bash", "make build", text);

        await Assert.That(preview).Contains("CONTAINS 'error:'");
        await Assert.That(preview).Contains("everything broke");
    }

    [Test]
    public async Task ErrorFlagPrefixesPreview()
    {
        string preview = PreviewRenderer.RenderPreview("Bash", "ls", "plain output", isError: true);

        await Assert.That(preview).StartsWith("[ERROR] ");
    }

    [Test]
    public async Task EditPreviewNamesThePathNotTheSuccessSentence()
    {
        string preview = PreviewRenderer.RenderPreview(
            "Edit", @"src\Claudinine\Foo.cs", "The file has been updated successfully.");

        await Assert.That(preview).Contains(@"applied to src\Claudinine\Foo.cs");
        await Assert.That(preview).DoesNotContain("has been updated");
    }

    [Test]
    public async Task ReadPreviewSkipsGutterAndPunctuationOnlyLines()
    {
        string text = "     1\t{\n     2\t  \"name\": \"claudinine\",\n     3\tclass Foo\n";

        string preview = PreviewRenderer.RenderPreview("Read", "package.json", text);

        await Assert.That(preview).DoesNotContain("     1"); // gutter stripped
        await Assert.That(preview).Contains("lines ::");
        await Assert.That(preview).Contains("\"name\": \"claudinine\""); // first informative line
    }

    [Test]
    public async Task JsonArrayDescribedByShapeNotPunctuation()
    {
        string text = """[{"id":1,"name":"a"},{"id":2,"name":"b"},{"id":3,"name":"c"}]""";

        string preview = PreviewRenderer.RenderPreview("mcp__something__list", "", text);

        await Assert.That(preview).Contains("JSON array, 3 item(s)");
        await Assert.That(preview).Contains("keys [id, name]");
    }

    [Test]
    public async Task JsonObjectDescribedByKeys()
    {
        string preview = PreviewRenderer.RenderPreview(
            "mcp__api__get", "", """{"status":"ok","count":42}""");

        await Assert.That(preview).Contains("JSON object");
        await Assert.That(preview).Contains("count");
        await Assert.That(preview).Contains("status");
    }

    [Test]
    public async Task SectionedOutputNamesEverySection()
    {
        string text = "=== hooks.json ===\nfirst body\n=== settings.json ===\nsecond body\n";

        string preview = PreviewRenderer.RenderPreview("Bash", "cat hooks.json settings.json", text);

        await Assert.That(preview).Contains("2 sections");
        await Assert.That(preview).Contains("hooks.json: first body");
        await Assert.That(preview).Contains("settings.json: second body");
    }

    [Test]
    public async Task GitStatusPreviewCountsLines()
    {
        string text = " M src/a.cs\n?? src/b.cs\n";

        string preview = PreviewRenderer.RenderPreview("Bash", "git status --short", text);

        await Assert.That(preview).Contains("2 status line(s)");
    }

    [Test]
    public async Task GitLogPreviewCountsCommits()
    {
        string text = "abc123 first\ndef456 second\nfed789 third\n";

        string preview = PreviewRenderer.RenderPreview("Bash", "git log --oneline -3", text);

        await Assert.That(preview).Contains("3 commit line(s)");
    }

    [Test]
    public async Task TailPipelineShowsTheTail()
    {
        string text = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"row {i}"));

        string preview = PreviewRenderer.RenderPreview("Bash", "cat data.txt | tail -5", text);

        await Assert.That(preview).StartsWith("tail ::");
        await Assert.That(preview).Contains("row 50");
    }

    [Test]
    public async Task EmptyOutputSaysSo()
    {
        await Assert.That(PreviewRenderer.RenderPreview("Bash", "true", "   \n  ")).IsEqualTo("(no output)");
    }

    [Test]
    public async Task BannerOnlyLinesNeverBecomeThePreview()
    {
        string text = "==========\nactual content here\n";

        string preview = PreviewRenderer.RenderPreview("Bash", "run.sh", text);

        await Assert.That(preview).Contains("actual content here");
        await Assert.That(preview).DoesNotContain("==========");
    }
}
