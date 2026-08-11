namespace OneCode.Core.IO;

/// <summary>
/// 系统剪贴板服务契约。
///
/// 统一剪贴板的读写能力：
/// - 写入：将文本写入系统剪贴板
/// - 读取：从剪贴板读取文本、文件列表或图像数据
///
/// 实现由 Infrastructure 层提供（ClipboardService）。
/// 调用方通过 DI 注入此接口，禁止直接使用具体实现或静态类。
/// </summary>
public interface IClipboardService
{
    /// <summary>
    /// 将 <paramref name="text"/> 复制到系统剪贴板。
    /// Windows 平台使用 PowerShell + UTF-8 临时文件以正确处理非 ASCII 字符
    /// （避免 Windows PowerShell 5.1 stdin OEM 编码乱码问题）。
    /// </summary>
    /// <returns><c>null</c> 表示成功；失败时返回错误信息字符串</returns>
    Task<string?> TryCopyTextAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// 从系统剪贴板读取文本。
    /// </summary>
    /// <returns>剪贴板文本内容；剪贴板为空或不可访问时返回 <c>null</c></returns>
    Task<string?> GetTextAsync(CancellationToken ct = default);

    /// <summary>
    /// 从系统剪贴板读取文件列表（例如 Explorer/Finder 中复制的文件）。
    /// </summary>
    /// <returns>文件路径列表；无文件数据时返回空列表</returns>
    Task<List<string>> GetFilesAsync(CancellationToken ct = default);

    /// <summary>
    /// 检查剪贴板是否包含图像数据（原始位图，非文件路径）。
    /// 若存在图像则保存到临时文件并返回路径。
    /// </summary>
    /// <returns>保存到临时文件的图像路径；剪贴板无图像或不可访问时返回 <c>null</c></returns>
    Task<string?> GetImageAsync(CancellationToken ct = default);
}
