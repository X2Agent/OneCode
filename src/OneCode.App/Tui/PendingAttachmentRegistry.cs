using System.Text.RegularExpressions;

namespace OneCode.App.Tui;

/// <summary>
/// 统一管理输入框中待提交的附件（折叠长文本与图片）。
/// 确保长文本折叠与图片附件在输入编辑、撤销和提交时的生命周期原子对齐，
/// 避免使用单一全局变量覆盖用户在占位符前后输入的提示词。
/// </summary>
internal sealed partial class PendingAttachmentRegistry
{
    private readonly Dictionary<int, TextFoldAttachment> _textFolds = new();
    private readonly Dictionary<int, ImageAttachment> _images = new();
    private int _nextId;

    [GeneratedRegex(@"\uE001(?:\[Pasted(?: text)? #(?<id>\d+)[^\]\uE002]*\]|#(?<id>\d+))\uE002")]
    private static partial Regex TextFoldTokenRegex();

    [GeneratedRegex(@"\[Image #(?<id>\d+)\]")]
    private static partial Regex ImageTagRegex();

    public int RegisterTextFold(string fullText, int lineCount)
    {
        var id = ++_nextId;
        _textFolds[id] = new TextFoldAttachment(id, fullText, lineCount);
        return id;
    }

    public int RegisterImage(string initialPath)
    {
        var id = ++_nextId;
        _images[id] = new ImageAttachment(id, initialPath);
        return id;
    }

    public void UpdateImagePath(int id, string processedPath)
    {
        if (_images.TryGetValue(id, out var img))
        {
            _images[id] = img with { FilePath = processedPath };
        }
    }

    public bool TryGetTextFold(int id, out TextFoldAttachment fold) =>
        _textFolds.TryGetValue(id, out fold!);

    public bool TryGetImage(int id, out ImageAttachment img) =>
        _images.TryGetValue(id, out img!);

    public int TextFoldCount => _textFolds.Count;
    public int ImageCount => _images.Count;
    public int TotalCount => _textFolds.Count + _images.Count;

    /// <summary>
    /// 生命周期对账：文本变化后，剔除其占位符已不在当前文本中的附件
    /// （编辑器内删除/撤销、全选重打、程序化整体替换等路径），
    /// 杜绝孤儿附件随提交发出。以「注册表 ∩ 文本」为准，手工键入的同形
    /// 占位符不会凭空注册附件，因此不会误保留。
    /// </summary>
    public void PruneMissing(string? text)
    {
        if (_textFolds.Count == 0 && _images.Count == 0)
            return;

        text ??= string.Empty;

        if (_textFolds.Count > 0)
        {
            var present = CollectIds(text, TextFoldTokenRegex());
            foreach (var id in _textFolds.Keys.Where(id => !present.Contains(id)).ToList())
                _textFolds.Remove(id);
        }

        if (_images.Count > 0)
        {
            var present = CollectIds(text, ImageTagRegex());
            foreach (var id in _images.Keys.Where(id => !present.Contains(id)).ToList())
                _images.Remove(id);
        }
    }

    private static HashSet<int> CollectIds(string text, Regex regex)
    {
        var ids = new HashSet<int>();
        foreach (Match m in regex.Matches(text))
        {
            if (int.TryParse(m.Groups["id"].Value, out var id))
                ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// Expands all Unicode PUA tokenized paste attachments back into their original text.
    /// Preserves all user-typed text before, between, and after folded attachments.
    /// Manually typed plain text placeholders (without \uE001 and \uE002) are left untouched.
    /// </summary>
    public string ExpandAttachments(string text)
    {
        if (string.IsNullOrEmpty(text) || _textFolds.Count == 0)
            return text;

        return TextFoldTokenRegex().Replace(text, match =>
        {
            var idStr = match.Groups["id"].Value;
            if (int.TryParse(idStr, out var id) && _textFolds.TryGetValue(id, out var fold))
            {
                return fold.FullText;
            }
            // 未知 id（如注册表已被 Prune/Clear）：保留原文而非静默删字，避免提交丢内容。
            return match.Value;
        });
    }

    public IReadOnlyList<string> TakeImages()
    {
        if (_images.Count == 0) return [];
        var paths = _images.OrderBy(kv => kv.Key).Select(kv => kv.Value.FilePath).ToList();
        _images.Clear();
        return paths;
    }

    public void Clear()
    {
        _textFolds.Clear();
        _images.Clear();
    }
}

internal sealed record TextFoldAttachment(int Id, string FullText, int LineCount);
internal sealed record ImageAttachment(int Id, string FilePath);
