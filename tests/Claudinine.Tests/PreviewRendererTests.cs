using Claudinine.Rules;
using Xunit;

namespace Claudinine.Tests;

/// <summary>
/// The digest preview heuristics — pure functions, heuristic-heavy, and the only
/// thing a future session sees without paying for retrieval. Each test pins the
/// verdict-over-head ordering the class doc promises.
/// </summary>
public class PreviewRendererTests
{
    [Fact]
    public void PytestSummaryWinsOverHead()
    {
        string text = "collected 12 items\n"
            + "test_a.py::test_one PASSED\n"
            + "FAILED test_b.py::test_two - AssertionError\n"
            + "==== 1 failed, 11 passed in 3.21s ====\n";

        string preview = PreviewRenderer.RenderPreview("Bash", "python -m pytest", text);

        Assert.Contains("RESULT: 1 failed, 11 passed", preview);
        Assert.Contains("first failures: test_b.py::test_two", preview);
    }

    [Fact]
    public void ErrorMarkerLineSurfacedNotHead()
    {
        string text = "step one ok\nstep two ok\nerror: everything broke\ntail line";

        string preview = PreviewRenderer.RenderPreview("Bash", "make build", text);

        Assert.Contains("CONTAINS 'error:'", preview);
        Assert.Contains("everything broke", preview);
    }

    [Fact]
    public void ErrorFlagPrefixesPreview()
    {
        string preview = PreviewRenderer.RenderPreview("Bash", "ls", "plain output", isError: true);

        Assert.StartsWith("[ERROR] ", preview);
    }

    [Fact]
    public void EditPreviewNamesThePathNotTheSuccessSentence()
    {
        string preview = PreviewRenderer.RenderPreview(
            "Edit", @"src\Claudinine\Foo.cs", "The file has been updated successfully.");

        Assert.Contains(@"applied to src\Claudinine\Foo.cs", preview);
        Assert.DoesNotContain("has been updated", preview);
    }

    [Fact]
    public void ReadPreviewSkipsGutterAndPunctuationOnlyLines()
    {
        string text = "     1\t{\n     2\t  \"name\": \"claudinine\",\n     3\tclass Foo\n";

        string preview = PreviewRenderer.RenderPreview("Read", "package.json", text);

        Assert.DoesNotContain("     1", preview); // gutter stripped
        Assert.Contains("lines ::", preview);
        Assert.Contains("\"name\": \"claudinine\"", preview); // first informative line
    }

    [Fact]
    public void JsonArrayDescribedByShapeNotPunctuation()
    {
        string text = """[{"id":1,"name":"a"},{"id":2,"name":"b"},{"id":3,"name":"c"}]""";

        string preview = PreviewRenderer.RenderPreview("mcp__something__list", "", text);

        Assert.Contains("JSON array, 3 item(s)", preview);
        Assert.Contains("keys [id, name]", preview);
    }

    [Fact]
    public void JsonObjectDescribedByKeys()
    {
        string preview = PreviewRenderer.RenderPreview(
            "mcp__api__get", "", """{"status":"ok","count":42}""");

        Assert.Contains("JSON object", preview);
        Assert.Contains("count", preview);
        Assert.Contains("status", preview);
    }

    [Fact]
    public void SectionedOutputNamesEverySection()
    {
        string text = "=== hooks.json ===\nfirst body\n=== settings.json ===\nsecond body\n";

        string preview = PreviewRenderer.RenderPreview("Bash", "cat hooks.json settings.json", text);

        Assert.Contains("2 sections", preview);
        Assert.Contains("hooks.json: first body", preview);
        Assert.Contains("settings.json: second body", preview);
    }

    [Fact]
    public void GitStatusPreviewCountsLines()
    {
        string text = " M src/a.cs\n?? src/b.cs\n";

        string preview = PreviewRenderer.RenderPreview("Bash", "git status --short", text);

        Assert.Contains("2 status line(s)", preview);
    }

    [Fact]
    public void GitLogPreviewCountsCommits()
    {
        string text = "abc123 first\ndef456 second\nfed789 third\n";

        string preview = PreviewRenderer.RenderPreview("Bash", "git log --oneline -3", text);

        Assert.Contains("3 commit line(s)", preview);
    }

    [Fact]
    public void TailPipelineShowsTheTail()
    {
        string text = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"row {i}"));

        string preview = PreviewRenderer.RenderPreview("Bash", "cat data.txt | tail -5", text);

        Assert.StartsWith("tail ::", preview);
        Assert.Contains("row 50", preview);
    }

    [Fact]
    public void EmptyOutputSaysSo()
    {
        Assert.Equal("(no output)", PreviewRenderer.RenderPreview("Bash", "true", "   \n  "));
    }

    [Fact]
    public void BannerOnlyLinesNeverBecomeThePreview()
    {
        string text = "==========\nactual content here\n";

        string preview = PreviewRenderer.RenderPreview("Bash", "run.sh", text);

        Assert.Contains("actual content here", preview);
        Assert.DoesNotContain("==========", preview);
    }
}
