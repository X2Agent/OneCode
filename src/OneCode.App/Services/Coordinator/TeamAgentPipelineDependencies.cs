using OneCode.App.Services.Agent;
using OneCode.App.Session;

namespace OneCode.App.Services.Coordinator;

public sealed record TeamAgentPipelineDependencies(
    AgentContextPipeline ContextPipeline,
    SubAgentPipelineFactory PipelineFactory,
    CompactionStrategyFactory CompactionBuilder,
    ISessionConversationAccess SessionAccess,
    Core.Tools.ToolMetadataRegistry ToolMetadata);
