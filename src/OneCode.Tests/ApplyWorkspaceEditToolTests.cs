using OneCode.App.Tools;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="ApplyWorkspaceEditTool.ApplyTextEdits"/>.
///
/// Covers the high-risk text-editing logic that LSP codeAction/rename results
/// depend on: bottom-to-top edit ordering, multi-line splicing, CRLF
/// preservation, out-of-range guards, and empty-file edge cases.
/// </summary>
public sealed class ApplyWorkspaceEditToolTests
{
    /// <summary>Convenience factory for <see cref="ApplyWorkspaceEditTool.TextEditInfo"/>.</summary>
    private static ApplyWorkspaceEditTool.TextEditInfo Edit(
        int startLine, int startChar, int endLine, int endChar, string newText)
        => new(startLine, startChar, endLine, endChar, newText);

    // Single-line edits

    [Fact]
    public void SingleLine_ReplaceSubstring_PreservesSurroundingText()
    {
        var content = "var x = old;";
        var edits = new List<ApplyWorkspaceEditTool.TextEditInfo>
        {
            Edit(0, 8, 0, 11, "new"),
        };

        var result = ApplyWorkspaceEditTool.ApplyTextEdits(content, edits);

        result.Should().Be("var x = new;");
    }

    [Fact]
    public void SingleLine_InsertAtPosition_ShiftsTrailingText()
    {
        var content = "Hello World";
        var edits = new List<ApplyWorkspaceEditTool.TextEditInfo>
        {
            Edit(0, 5, 0, 5, " Beautiful"),
        };

        var result = ApplyWorkspaceEditTool.ApplyTextEdits(content, edits);

        result.Should().Be("Hello Beautiful World");
    }

    [Fact]
    public void SingleLine_ReplaceEntireLine()
    {
        var content = "line one\nline two\nline three";
        var edits = new List<ApplyWorkspaceEditTool.TextEditInfo>
        {
            Edit(1, 0, 1, 8, "replaced"),
        };

        var result = ApplyWorkspaceEditTool.ApplyTextEdits(content, edits);

        result.Should().Be("line one\nreplaced\nline three");
    }

    // Multi-line edits

    [Fact]
    public void MultiLine_ReplaceAcrossLines_SplicesCorrectly()
    {
        var content = "void A()\n{\n    Old();\n}";
        // Replace from start of line 2 ("    Old();") through end of line 3 ("}")
        var edits = new List<ApplyWorkspaceEditTool.TextEditInfo>
        {
            Edit(2, 0, 3, 1, "    New();\n}"),
        };

        var result = ApplyWorkspaceEditTool.ApplyTextEdits(content, edits);

        result.Should().Be("void A()\n{\n    New();\n}");
    }

    [Fact]
    public void MultiLine_NewTextExpandsToMoreLines()
    {
        var content = "start\nend";
        var edits = new List<ApplyWorkspaceEditTool.TextEditInfo>
        {
            Edit(0, 5, 1, 0, "\nmiddle\n"),
        };

        var result = ApplyWorkspaceEditTool.ApplyTextEdits(content, edits);

        result.Should().Be("start\nmiddle\nend");
    }

    [Fact]
    public void MultiLine_NewTextCollapsesLines()
    {
        var content = "a\nb\nc\nd";
        // Replace lines 1-2 ("b\nc") with single "x"
        var edits = new List<ApplyWorkspaceEditTool.TextEditInfo>
        {
            Edit(1, 0, 2, 1, "x"),
        };

        var result = ApplyWorkspaceEditTool.ApplyTextEdits(content, edits);

        result.Should().Be("a\nx\nd");
    }

    // Multiple edits applied bottom-to-top

    [Fact]
    public void MultipleEdits_AppliedBottomToTop_PreservesOffsets()
    {
        // Edit on line 0 inserts a new line — if edits were applied top-to-bottom,
        // line 2's position would shift and the second edit would target the wrong line.
        // Bottom-to-top ordering guarantees earlier edits don't affect later edits' positions.
        var content = "line1\nline2\nline3";
        var edits = new List<ApplyWorkspaceEditTool.TextEditInfo>
        {
            Edit(0, 5, 0, 5, "\nINSERTED"),  // line1 → line1\nINSERTED (adds a line)
            Edit(2, 0, 2, 5, "CHANGED"),     // line3 → CHANGED
        };

        var result = ApplyWorkspaceEditTool.ApplyTextEdits(content, edits);

        result.Should().Be("line1\nINSERTED\nline2\nCHANGED");
    }

    [Fact]
    public void MultipleEdits_DifferentPositions_NoInterference()
    {
        var content = "aaa\nbbb\nccc";
        var edits = new List<ApplyWorkspaceEditTool.TextEditInfo>
        {
            Edit(0, 1, 0, 2, "X"),  // aXa
            Edit(2, 1, 2, 2, "Y"),  // cYc
        };

        var result = ApplyWorkspaceEditTool.ApplyTextEdits(content, edits);

        result.Should().Be("aXa\nbbb\ncYc");
    }

    // CRLF preservation

    [Fact]
    public void CRLF_SingleEdit_PreservesCRLFLineEndings()
    {
        var content = "var x = old;\r\nvar y = 2;";
        var edits = new List<ApplyWorkspaceEditTool.TextEditInfo>
        {
            Edit(0, 8, 0, 11, "new"),
        };

        var result = ApplyWorkspaceEditTool.ApplyTextEdits(content, edits);

        result.Should().Be("var x = new;\r\nvar y = 2;");
    }

    [Fact]
    public void CRLF_MultiLineEdit_DoesNotDoubleCarriageReturns()
    {
        // Regression: without LF-normalization before Split('\n'), each line
        // would retain a trailing \r, and string.Join("\r\n", lines) would
        // produce \r\r\n — corrupting the file.
        var content = "void A()\r\n{\r\n    Old();\r\n}";
        var edits = new List<ApplyWorkspaceEditTool.TextEditInfo>
        {
            Edit(2, 0, 3, 1, "    New();\n}"),
        };

        var result = ApplyWorkspaceEditTool.ApplyTextEdits(content, edits);

        result.Should().Be("void A()\r\n{\r\n    New();\r\n}");
    }

    [Fact]
    public void CRLF_NewTextWithLF_GetsExpandedToCROnJoin()
    {
        var content = "a\r\nb";
        var edits = new List<ApplyWorkspaceEditTool.TextEditInfo>
        {
            Edit(0, 1, 1, 0, "\ninserted\n"),
        };

        var result = ApplyWorkspaceEditTool.ApplyTextEdits(content, edits);

        result.Should().Be("a\r\ninserted\r\nb");
    }

    // Edge cases

    [Fact]
    public void EmptyFile_SingleInsert_ProducesContent()
    {
        var content = "";
        var edits = new List<ApplyWorkspaceEditTool.TextEditInfo>
        {
            Edit(0, 0, 0, 0, "hello"),
        };

        var result = ApplyWorkspaceEditTool.ApplyTextEdits(content, edits);

        // Empty string splits to [""] — single line at index 0.
        // Insert at (0,0,0,0) replaces nothing, prepends "hello".
        result.Should().Be("hello");
    }

    [Fact]
    public void EmptyEditsList_ReturnsOriginalContent()
    {
        var content = "unchanged\ncontent";
        var edits = new List<ApplyWorkspaceEditTool.TextEditInfo>();

        var result = ApplyWorkspaceEditTool.ApplyTextEdits(content, edits);

        result.Should().Be(content);
    }

    [Fact]
    public void OutOfRange_StartLineBeyondFile_SkipsEdit()
    {
        var content = "only one line";
        var edits = new List<ApplyWorkspaceEditTool.TextEditInfo>
        {
            Edit(5, 0, 5, 4, "ignored"),
        };

        var result = ApplyWorkspaceEditTool.ApplyTextEdits(content, edits);

        result.Should().Be("only one line");
    }

    [Fact]
    public void OutOfRange_EndLineClampedToLastLine()
    {
        var content = "line0\nline1";
        // EndLine 99 is clamped to last line index (1). With EndChar=0 the
        // suffix is the entirety of line1, so "replaced" is followed by "line1".
        // This documents the current clamp semantics: out-of-range EndLine is
        // clamped to the last line, and EndChar=0 means "start of that line",
        // so the remainder of the clamped line is preserved.
        var edits = new List<ApplyWorkspaceEditTool.TextEditInfo>
        {
            Edit(1, 0, 99, 0, "replaced"),
        };

        var result = ApplyWorkspaceEditTool.ApplyTextEdits(content, edits);

        result.Should().Be("line0\nreplacedline1");
    }

    [Fact]
    public void StartCharBeyondLineLength_ClampedSafely()
    {
        // Line 0 is only 5 chars long; StartChar=99 should be clamped.
        var content = "hello\nworld";
        var edits = new List<ApplyWorkspaceEditTool.TextEditInfo>
        {
            Edit(0, 99, 0, 99, "X"),
        };

        var result = ApplyWorkspaceEditTool.ApplyTextEdits(content, edits);

        // Clamped to end of line 0 → "helloX"
        result.Should().Be("helloX\nworld");
    }

    [Fact]
    public void NewTextEmpty_DeletesRange()
    {
        var content = "keep this\nremove this\nkeep this too";
        var edits = new List<ApplyWorkspaceEditTool.TextEditInfo>
        {
            Edit(1, 0, 2, 0, ""),
        };

        var result = ApplyWorkspaceEditTool.ApplyTextEdits(content, edits);

        result.Should().Be("keep this\nkeep this too");
    }

    [Fact]
    public void LFOnlyFile_PreservesLFLineEndings()
    {
        var content = "line1\nline2\nline3";
        var edits = new List<ApplyWorkspaceEditTool.TextEditInfo>
        {
            Edit(1, 0, 1, 5, "replaced"),
        };

        var result = ApplyWorkspaceEditTool.ApplyTextEdits(content, edits);

        result.Should().Be("line1\nreplaced\nline3");
        result.Should().NotContain("\r");
    }

    [Fact]
    public void FullFileReplace_SingleEditCoveringEverything()
    {
        var content = "old content\nsecond line";
        var edits = new List<ApplyWorkspaceEditTool.TextEditInfo>
        {
            Edit(0, 0, 1, 11, "brand new content"),
        };

        var result = ApplyWorkspaceEditTool.ApplyTextEdits(content, edits);

        result.Should().Be("brand new content");
    }
}
