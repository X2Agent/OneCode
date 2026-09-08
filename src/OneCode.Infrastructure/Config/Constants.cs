using OneCode.Core.Product;

namespace OneCode.Infrastructure.Config;

public static partial class Constants
{
    public static class App
    {
        public static string Name => ProductInfo.Default.Name;
        public static string CommandLine => ProductInfo.Default.CommandName;
        public static string ConfigDirName => ProductInfo.Default.ConfigDirName;

        /// <summary>
        /// Configuration directory name candidates in descending priority order.
        /// Read/discover/watch actions iterate all candidates (see <see cref="ConfigDirPaths"/>);
        /// write actions keep using <see cref="ConfigDirName"/> (the primary candidate) so the
        /// install location stays stable.
        /// </summary>
        public static IReadOnlyList<string> ConfigDirCandidates { get; } =
            [".onecode", ".agent", ".claude"];

        public const string SettingsFileName = "settings.json";
        public static string ConfigFileRelative => $"{ProductInfo.Default.ConfigDirName}/{SettingsFileName}";
    }

    public static class HttpClientNames
    {
        public const string McpRegistry = "OneCode.McpRegistry";
        public const string WebSearch = "WebSearch";
        /// <summary>Named client for WebFetch (auto-redirect disabled, tool drives redirects).</summary>
        public const string WebFetch = "WebFetch";
        /// <summary>Named client for HTTP hook execution (generic user-defined HTTP calls).</summary>
        public const string HookHttp = "HookHttp";
        public const string Upgrade = "upgrade";
        public const string ModelsDev = "ModelsDev";
        /// <summary>Named client for Ollama native API (infinite timeout, identity UA).</summary>
        public const string Ollama = "OneCode.Ollama";
        /// <summary>Named client for OpenAI-compatible APIs (infinite timeout, sanitizing + identity).</summary>
        public const string OpenAI = "OneCode.OpenAI";
    }

    public static class Urls
    {
        /// <summary>Official MCP Registry (Anthropic/GitHub/PulseMCP/Microsoft), unauthenticated read-only API.</summary>
        public const string McpRegistry = "https://registry.modelcontextprotocol.io";
        /// <summary>Tavily Search API 端点（Bearer 认证，POST JSON）。</summary>
        public const string TavilySearch = "https://api.tavily.com/search";
    }

    public static class Subdirs
    {
        public const string Skills = "skills";
        public const string Prompts = "prompts";
        public const string Commands = "commands";
        public const string Cache = "cache";
    }

    public static class Timeouts
    {
        /// <summary>MCP registry 命名客户端超时：官方 search 端点实测 18~26s 返回，
        /// 必须大于客户端内置 30s 请求预算，避免 HttpClient 层先掐断慢响应。</summary>
        public const int McpRegistry = 35;
        public const int WebSearch = 20;
        public const int ModelsDev = 30;
    }
}
