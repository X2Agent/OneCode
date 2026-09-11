using OneCode.App.Tui;

namespace OneCode.Tests;

public sealed class PendingAttachmentRegistryTests
{
    [Fact]
    public void RegisterTextFold_AssignsIncrementalIds_AndStoresContent()
    {
        var registry = new PendingAttachmentRegistry();

        var id1 = registry.RegisterTextFold("line1\nline2\nline3\nline4\nline5", 5);
        var id2 = registry.RegisterTextFold("another\ntext\nwith\nmultiple\nlines", 5);

        id1.Should().Be(1);
        id2.Should().Be(2);
        registry.TextFoldCount.Should().Be(2);

        registry.TryGetTextFold(id1, out var fold1).Should().BeTrue();
        fold1.FullText.Should().Be("line1\nline2\nline3\nline4\nline5");
        fold1.LineCount.Should().Be(5);

        registry.TryGetTextFold(999, out _).Should().BeFalse();
    }

    [Fact]
    public void ImageLifecycle_RegisterUpdateAndTake_PreservesOrderAndClears()
    {
        var registry = new PendingAttachmentRegistry();

        var id1 = registry.RegisterImage("temp/img1.png");
        var id2 = registry.RegisterImage("temp/img2.png");

        id1.Should().Be(1);
        id2.Should().Be(2);
        registry.ImageCount.Should().Be(2);

        // Update with processed path
        registry.UpdateImagePath(id1, "processed/img1_compressed.png");

        var images = registry.TakeImages();
        images.Should().HaveCount(2);
        images[0].Should().Be("processed/img1_compressed.png");
        images[1].Should().Be("temp/img2.png");

        // After taking, image count should be 0
        registry.ImageCount.Should().Be(0);
        registry.TakeImages().Should().BeEmpty();
    }

    [Fact]
    public void Clear_EmptiesBothFoldsAndImages()
    {
        var registry = new PendingAttachmentRegistry();
        registry.RegisterTextFold("some code", 10);
        registry.RegisterImage("some/path.png");

        registry.TotalCount.Should().Be(2);

        registry.Clear();

        registry.TotalCount.Should().Be(0);
        registry.TextFoldCount.Should().Be(0);
        registry.ImageCount.Should().Be(0);
    }

    [Fact]
    public void ExpandAttachments_WithPrefixAndSuffix_PreservesUserTextAndReplacesPuaToken()
    {
        var registry = new PendingAttachmentRegistry();
        var id = registry.RegisterTextFold("public void DoWork() { return; }", 1);

        var input = $"Please refactor: \uE001[Pasted text #{id} +10 lines]\uE002 and ensure tests pass.";
        var result = registry.ExpandAttachments(input);

        result.Should().Be("Please refactor: public void DoWork() { return; } and ensure tests pass.");
    }

    [Fact]
    public void ExpandAttachments_UserHandTypedPlaceholder_DoesNotExpand()
    {
        var registry = new PendingAttachmentRegistry();
        registry.RegisterTextFold("secret pasted code", 5);

        // Hand-typed by user without \uE001 and \uE002 control characters
        var input = "Check this placeholder: [Pasted text #1 +10 lines] and [Pasted #1]";
        var result = registry.ExpandAttachments(input);

        // Must remain untouched — no accidental collision with user hand-typed text
        result.Should().Be("Check this placeholder: [Pasted text #1 +10 lines] and [Pasted #1]");
    }

    [Fact]
    public void ExpandAttachments_MultipleFolds_ReplacesBothCorrectly()
    {
        var registry = new PendingAttachmentRegistry();
        var id1 = registry.RegisterTextFold("int a = 1;", 1);
        var id2 = registry.RegisterTextFold("int b = 2;", 1);

        var input = $"Compare \uE001[Pasted text #{id1} +1 lines]\uE002 with \uE001[Pasted text #{id2} +1 lines]\uE002.";
        var result = registry.ExpandAttachments(input);

        result.Should().Be("Compare int a = 1; with int b = 2;.");
    }

    [Fact]
    public void PruneMissing_RemovesFoldsAndImagesWhoseTokensLeftTheText()
    {
        var registry = new PendingAttachmentRegistry();
        var foldId = registry.RegisterTextFold("folded code", 5);
        var imageId = registry.RegisterImage("temp/img.png");

        // 全选重打 / 程序化整体替换：占位符全部消失
        registry.PruneMissing("brand new prompt text");

        registry.TryGetTextFold(foldId, out _).Should().BeFalse("占位符已不在文本中，折叠必须剔除");
        registry.TryGetImage(imageId, out _).Should().BeFalse("图片标签已不在文本中，附件必须剔除");
        registry.TotalCount.Should().Be(0);
    }

    [Fact]
    public void PruneMissing_KeepsAttachmentsWhoseTokensStillPresent()
    {
        var registry = new PendingAttachmentRegistry();
        var foldId = registry.RegisterTextFold("folded code", 5);
        var imageId = registry.RegisterImage("temp/img.png");

        var text = $"fix \uE001[Pasted text #{foldId} +5 lines]\uE002 using [Image #{imageId}] please";
        registry.PruneMissing(text);

        registry.TryGetTextFold(foldId, out _).Should().BeTrue();
        registry.TryGetImage(imageId, out _).Should().BeTrue();
    }

    [Fact]
    public void PruneMissing_HandTypedImageTagWithoutRegistration_HasNoEffect()
    {
        var registry = new PendingAttachmentRegistry();
        registry.RegisterImage("temp/real.png");
        var realId = 1;

        // 文本中的 [Image #99] 未注册——对账只做交集裁剪，不凭空注册
        registry.PruneMissing($"look [Image #99] and [Image #{realId}]");

        registry.ImageCount.Should().Be(1);
        registry.TryGetImage(realId, out var img).Should().BeTrue();
        img.FilePath.Should().Be("temp/real.png");
    }
}
