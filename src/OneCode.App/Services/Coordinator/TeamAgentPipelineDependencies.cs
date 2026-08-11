using OneCode.App.Services.Agent;
using OneCode.App.Session;

namespace OneCode.App.Services.Coordinator;

public sealed record TeamAgentPipelineDependencies(
    SharedContextProviderBuilder SharedContextBuilder,
    SubAgentPipelineFactory PipelineFactory,
    CompactionProviderBuilder CompactionBuilder,
    ISessionConversationAccess SessionAccess);
