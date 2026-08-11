namespace OneCode.Infrastructure;

/// <summary>
/// VCR fixture 存储路径集中管理。
/// 所有 VCR fixture 都位于 <c>~/.onecode/vcr/</c> 下，按捕获层分为 chat（语义层）与 http（HTTP 层）。
/// </summary>
public static class VcrPaths
{
    private static readonly string VcrRoot = Path.Combine(
        PathsHelper.GetUserConfigDir(), "vcr");

    /// <summary>语义层（<see cref="OneCode.Infrastructure.Ai.VcrChatClientDecorator"/>) fixture 目录。</summary>
    public static string ChatFixturesDir => Path.Combine(VcrRoot, "chat");

    /// <summary>HTTP 层（<see cref="VcrDelegatingHandler"/>) fixture 目录。</summary>
    public static string HttpFixturesDir => Path.Combine(VcrRoot, "http");
}
