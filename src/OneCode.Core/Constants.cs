namespace OneCode.Core;

public static class Constants
{
    public static class EnvVars
    {
        public const string OneCodeApiKey = "ONECODE_API_KEY";
        public const string OneCodeBaseUrl = "ONECODE_BASE_URL";
        public const string OneCodeModel = "ONECODE_MODEL";
        /// <summary>Tavily 搜索 API Key（webSearchApiKeys.tavily 的环境变量映射，免费额度注册即得）。</summary>
        public const string TavilyApiKey = "TAVILY_API_KEY";
        /// <summary>WebSearch 主提供方（tavily | duckduckgo）。</summary>
        public const string OneCodeWebSearchProvider = "ONECODE_WEB_SEARCH_PROVIDER";
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
        public const string MaxBudgetTokens = "maxBudgetTokens";
        /// <summary>Goal 模式：跨子目标总 attempt 上限。</summary>
        public const string GoalMaxSubGoalAttempts = "goal.maxSubGoalAttempts";
        /// <summary>Goal 模式：单子目标轮数上限。</summary>
        public const string GoalMaxTurnsPerSubGoal = "goal.maxTurnsPerSubGoal";
        /// <summary>Goal 模式：累计 token 上限（≤0 表示不限制）。</summary>
        public const string GoalMaxTotalTokens = "goal.maxTotalTokens";
        /// <summary>Goal 模式：墙钟上限小时数（≤0 表示不限制）。</summary>
        public const string GoalMaxWallClockHours = "goal.maxWallClockHours";
        /// <summary>Loop（/loop 运行时循环）：单次循环调用 inner agent 的硬上限。</summary>
        public const string LoopMaxIterations = "loop.maxIterations";
        public const string NextPromptSuggesterEnabled = "nextPromptSuggesterEnabled";
        public const string NotificationsEnabled = "notificationsEnabled";
        public const string OllamaContextWindow = "ollamaContextWindow";
        /// <summary>WebSearch 主提供方（tavily | duckduckgo），决定搜索链路首选提供者。</summary>
        public const string WebSearchProvider = "webSearchProvider";
        /// <summary>Tavily 搜索 API Key（密钥，允许项目作用域）。</summary>
        public const string TavilyApiKey = "webSearchApiKeys.tavily";
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
        public const long MaxBudgetTokensDefault = 5_000_000;
        /// <summary>会话事件 JSONL 文件扩展名（不含点）。</summary>
        public const string EventsFileExtension = "jsonl";
        /// <summary>GOAL：跨子目标总 attempt 上限默认值（<c>goal.maxSubGoalAttempts</c>）。</summary>
        public const int GoalMaxSubGoalAttemptsDefault = 20;
        /// <summary>GOAL：单子目标轮数上限默认值（<c>goal.maxTurnsPerSubGoal</c>）。</summary>
        public const int GoalMaxTurnsPerSubGoalDefault = 50;
        /// <summary>GOAL：累计 token 上限默认值（<c>goal.maxTotalTokens</c>，≤0 表示不限制）。</summary>
        public const long GoalMaxTotalTokensDefault = 200_000;
        /// <summary>GOAL：墙钟上限小时数默认值（<c>goal.maxWallClockHours</c>，≤0 表示不限制）。</summary>
        public const double GoalMaxWallClockHoursDefault = 2.0;
        /// <summary>/loop 单次循环调用 inner agent 次数默认值（<c>loop.maxIterations</c>）。</summary>
        public const int LoopMaxIterationsDefault = 3;
    }

    public static class PermissionModes
    {
        public const string Default = "default";
    }


    public static class MessageTypes
    {
        public const string User = "user";
        public const string Assistant = "assistant";
    }
}
