namespace OneCode.Core;

public static class Constants
{
    public static class EnvVars
    {
        public const string OneCodeApiKey = "ONECODE_API_KEY";
        public const string OneCodeBaseUrl = "ONECODE_BASE_URL";
        public const string OneCodeModel = "ONECODE_MODEL";
        /// <summary>VCR 录像/回放模式开关。值为 off/空(不激活)、replay(回放)、record(录像)。</summary>
        public const string Vcr = "ONECODE_VCR";
        public const string BraveSearchApiKey = "BRAVE_SEARCH_API_KEY";
        public const string OneCodeWebSearchProvider = "ONECODE_WEB_SEARCH_PROVIDER";
        public const string OneCodeWebSearchApiKey = "ONECODE_WEB_SEARCH_API_KEY";
        public const string Home = "HOME";
        public const string UserHomeWindows = "USERPROFILE";
        public const string NoProxy = "NO_PROXY";
        public const string NoProxyLower = "no_proxy";
        public const string HttpProxy = "HTTP_PROXY";
        public const string HttpProxyLower = "http_proxy";
        public const string HttpsProxy = "HTTPS_PROXY";
        public const string HttpsProxyLower = "https_proxy";
        /// <summary>
        /// 调试日志级别（单开关）：off | debug | trace。
        /// 空/未设置 → 按构建默认（DEBUG 构建开、Release 关）；显式 off/0/false 可关闭（含 DEBUG 构建）；
        /// debug/1 → Debug 级别；trace/2 → Trace 级别（隐含开启调试），在 Release 构建也强制生效。
        /// 其他未知值回退到构建默认。
        /// </summary>
        public const string LogLevel = "ONECODE_LOG_LEVEL";
        /// <summary>Worker 子代理标志：为 1/true 时表示为 Worker 进程。</summary>
        public const string IsWorker = "ONECODE_IS_WORKER";
        /// <summary>覆盖上下文窗口上限（token 数，最终覆盖手段）。</summary>
        public const string MaxContextTokens = "ONECODE_MAX_CONTEXT_TOKENS";
    }

    public static class ConfigKeys
    {
        public const string ApiKey = "apiKey";
        public const string BaseUrl = "baseUrl";
        public const string Model = "model";
        public const string Provider = "provider";
        public const string FastModel = "fastModel";
        public const string PermissionMode = "permissionMode";
        public const string MaxTurns = "maxTurns";
        public const string MaxBudgetUsd = "maxBudgetUsd";
        public const string NextPromptSuggesterEnabled = "nextPromptSuggesterEnabled";
        public const string NotificationsEnabled = "notificationsEnabled";
        public const string OllamaContextWindow = "ollamaContextWindow";
    }

    public static class ModelProviders
    {
        public const string Anthropic = "anthropic";
        public const string OpenAI = "openai";
        public const string Ollama = "ollama";
    }

    public static class Session
    {
        public const int MaxTurnsDefault = 100;
        public const double MaxBudgetUsdDefault = 10.0;
        public const string SessionFileExtension = ".jsonl";
    }

    public static class PermissionModes
    {
        public const string Default = "default";
        public const string BypassPermissions = "bypassPermissions";
        public const string Plan = "plan";
        /// <summary>YOLO 自动分类模式（启用 LLM 安全分类器）。</summary>
        public const string Auto = "auto";
        /// <summary>AcceptEdits 模式（文件写入 + 常规 Shell 自动放行）。</summary>
        public const string AcceptEdits = "acceptEdits";
    }


    public static class MessageTypes
    {
        public const string User = "user";
        public const string Assistant = "assistant";
    }
}
