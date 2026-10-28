using OneCode.App.Session;
using OneCode.Core;
using OneCode.Core.Models;
using OneCode.Core.Tokens;

namespace OneCode.App.Services.Compact;

/// <summary>
/// Session + model surfaces for explicit /compact.
/// <paramref name="TokenLedger"/>：显式压缩直连 chatClient（不经 UsageTrackingRunMiddleware），
/// 经此把摘要消耗补记入会话桶，避免预算熔断与 /status stats 漏计。
/// </summary>
public sealed record CompactSessionDependencies(
    ISessionConversationAccess SessionAccess,
    ISessionManager SessionManager,
    IModelManager ModelManager,
    ITokenEstimator TokenEstimator,
    ITokenLedger? TokenLedger = null);
