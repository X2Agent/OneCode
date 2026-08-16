using OneCode.App.Session;
using OneCode.Core;
using OneCode.Core.Models;

namespace OneCode.App.Services.Compact;

/// <summary>Session + model surfaces for explicit /compact.</summary>
public sealed record CompactSessionDependencies(
    ISessionConversationAccess SessionAccess,
    ISessionManager SessionManager,
    IModelManager ModelManager,
    ITokenEstimator TokenEstimator);
