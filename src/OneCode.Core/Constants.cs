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
        /// <summary>启用持久化 Cron 任务开关（为 true 时允许 --durable 落盘）。</summary>
        public const string DurableCron = "ONECODE_DURABLE_CRON";
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
        /// <summary>子代理：默认轮数上限（≤0 = 按 profile 内置默认值）。</summary>
        public const string AgentsDefaultMaxTurns = "agents.defaults.maxTurns";
        /// <summary>子代理：默认墙钟超时分钟数（≤0 = 不限制）。</summary>
        public const string AgentsDefaultTimeoutMinutes = "agents.defaults.timeoutMinutes";
        public const string NextPromptSuggesterEnabled = "nextPromptSuggesterEnabled";
        public const string NotificationsEnabled = "notificationsEnabled";
        public const string OllamaContextWindow = "ollamaContextWindow";
        /// <summary>Ollama 采样温度（默认 0.15，抑制小模型 tool-call JSON 幻觉；重启后生效）。</summary>
        public const string OllamaTemperature = "ollama.temperature";
        /// <summary>流式 chunk 间空闲停滞判定上限（秒）：收到过数据后连续无新 chunk 超过该时长即重试。</summary>
        public const string StreamIdleTimeoutSeconds = "streamIdleTimeoutSeconds";
        /// <summary>流式请求过载/停滞的最大重试次数；最坏反馈时长约为（次数+1）×空闲上限。</summary>
        public const string StreamMaxRetries = "streamMaxRetries";
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
        /// <summary>只读调研子代理（Explore/Plan）默认轮数上限——每轮可并行多调用，
        /// 15 轮足以覆盖典型调研；更高预算由 frontmatter / settings 显式上调。</summary>
        public const int SubAgentMaxTurnsReadOnlyDefault = 15;
        /// <summary>通用子代理（general-purpose/自定义）默认轮数上限。</summary>
        public const int SubAgentMaxTurnsDefault = 50;
        /// <summary>子代理墙钟超时默认分钟数（<c>agents.defaults.timeoutMinutes</c>）。</summary>
        public const int SubAgentTimeoutMinutesDefault = 10;
    }
}
