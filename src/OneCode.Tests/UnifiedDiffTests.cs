using OneCode.Infrastructure.Text;

namespace OneCode.Tests;

/// <summary>
/// Tests for <see cref="UnifiedDiff"/>
/// </summary>
public sealed class UnifiedDiffTests
{
    [Fact]
    public void Compute_IdenticalContent_ReturnsNoChangesMessage()
    {
        var content = "line1\nline2\nline3";

        var result = UnifiedDiff.Compute(content, content, "test.txt");

        result.Should().Contain("no changes");
    }

    [Fact]
    public void Compute_AddedLine_ContainsPlusPrefix()
    {
        var original = "line1\nline2";
        var modified = "line1\nline2\nline3";

        var result = UnifiedDiff.Compute(original, modified, "test.txt");

        result.Should().Contain("+line3");
    }

    [Fact]
    public void Compute_RemovedLine_ContainsMinusPrefix()
    {
        var original = "line1\nline2\nline3";
        var modified = "line1\nline2";

        var result = UnifiedDiff.Compute(original, modified, "test.txt");

        result.Should().Contain("-line3");
    }

    [Fact]
    public void Compute_ModifiedLine_ContainsBothMinusAndPlus()
    {
        var original = "line1\nline2\nline3";
        var modified = "line1\nmodified\nline3";

        var result = UnifiedDiff.Compute(original, modified, "test.txt");

        result.Should().Contain("-line2");
        result.Should().Contain("+modified");
    }

    [Fact]
    public void Compute_IncludesFilePathInHeader()
    {
        var original = "line1";
        var modified = "line2";

        var result = UnifiedDiff.Compute(original, modified, "path/to/file.txt");

        result.Should().Contain("--- a/path/to/file.txt");
        result.Should().Contain("+++ b/path/to/file.txt");
    }

    [Fact]
    public void Compute_IncludesHunkHeader()
    {
        var original = "line1\nline2\nline3";
        var modified = "line1\nmodified\nline3";

        var result = UnifiedDiff.Compute(original, modified, "test.txt");

        result.Should().Contain("@@ -");
        result.Should().Contain("@@");
    }

    [Fact]
    public void Compute_LargeFileExceedingMaxLines_ReturnsSkippedMessage()
    {
        var largeContent = string.Join("\n", Enumerable.Range(0, 2001).Select(i => $"line{i}"));
        var modified = largeContent + "\nextra";

        var result = UnifiedDiff.Compute(largeContent, modified, "large.txt");

        result.Should().Contain("diff skipped");
        result.Should().Contain("exceeds");
    }

    [Fact]
    public void Compute_EmptyOriginalToContent_ShowsAllLinesAsAdded()
    {
        var original = "";
        var modified = "line1\nline2";

        var result = UnifiedDiff.Compute(original, modified, "test.txt");

        result.Should().Contain("+line1");
        result.Should().Contain("+line2");
    }

    [Fact]
    public void Compute_ContentToEmpty_ShowsAllLinesAsRemoved()
    {
        var original = "line1\nline2";
        var modified = "";

        var result = UnifiedDiff.Compute(original, modified, "test.txt");

        result.Should().Contain("-line1");
        result.Should().Contain("-line2");
    }

    [Fact]
    public void Compute_MultipleHunks_GeneratesMultipleHunkHeaders()
    {
        var original = "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9\nline10";
        var modified = "line1\nmodified2\nline3\nline4\nline5\nline6\nline7\nline8\nmodified9\nline10";

        var result = UnifiedDiff.Compute(original, modified, "test.txt");

        var hunkCount = result.Split("@@").Length - 1;
        hunkCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Compute_ContextLines_IncludesUnchangedLinesAroundChanges()
    {
        var original = "line1\nline2\nline3\nline4\nline5";
        var modified = "line1\nline2\nmodified3\nline4\nline5";

        var result = UnifiedDiff.Compute(original, modified, "test.txt", contextLines: 2);

        result.Should().Contain(" line2");
        result.Should().Contain(" line4");
    }
}


