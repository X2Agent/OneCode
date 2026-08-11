namespace OneCode.App.Services.Coordinator;

public sealed record TeamAgentToolSources(
    ICacheSafeParamsProvider CacheSafeParams,
    IToolCatalog ToolCatalog);
