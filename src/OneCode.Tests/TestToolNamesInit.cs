using System.Runtime.CompilerServices;
using OneCode.Core.Tools;

namespace OneCode.Tests;

/// <summary>
/// Initializes ToolNames with a standard tool registry before any tests run.
/// This replaces the old hardcoded HashSet approach with metadata-driven lookups.
/// </summary>
internal static class TestToolNamesInit
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var registry = new ToolMetadataRegistry();

        // ReadOnly tools
        foreach (var name in new[] { "Read", "Glob", "Grep", "LS", "WebFetch", "WebSearch",
                                     "Lsp", "BackgroundWait",
                                     "CronList", "AskUserQuestion", "Git" })
        {
            registry.Register(new ToolMetadata { Name = name, Risk = ToolRisk.ReadOnly });
        }

        // File edit/write tools
        registry.Register(new ToolMetadata
        {
            Name = "Write",
            Risk = ToolRisk.Destructive,
            Category = ToolCategory.FileEdit | ToolCategory.FileWrite,
        });
        registry.Register(new ToolMetadata
        {
            Name = "Edit",
            Risk = ToolRisk.Destructive,
            Category = ToolCategory.FileEdit | ToolCategory.FileWrite,
        });
        registry.Register(new ToolMetadata
        {
            Name = "ApplyWorkspaceEdit",
            Risk = ToolRisk.Destructive,
            Category = ToolCategory.FileWrite,
        });

        // Plan-allowed tools (beyond read-only)
        foreach (var name in new[] { "SubmitPlan", "Task", "Agent", "ParallelAgents" })
        {
            registry.Register(new ToolMetadata
            {
                Name = name,
                Risk = ToolRisk.Safe,
                Category = ToolCategory.PlanAllowed,
            });
        }
        // User-question tool is both ReadOnly and PlanAllowed.
        foreach (var name in new[] { "AskUserQuestion" })
        {
            registry.Register(new ToolMetadata
            {
                Name = name,
                Risk = ToolRisk.ReadOnly,
                Category = ToolCategory.PlanAllowed,
            });
        }

        // Shell tools
        registry.Register(new ToolMetadata { Name = "Bash", Risk = ToolRisk.Dynamic });

        // Harness 注入工具（todos_* / file_memory_*）：只读写 agent 会话状态，Plan 模式放行。
        // 生产路径经 HarnessProviderTools.RegisterMetadata 登记同一类别声明（见 AgentPipelineBuilder）。
        foreach (var name in OneCode.Infrastructure.Agent.HarnessProviderTools.Names)
        {
            registry.Register(new ToolMetadata
            {
                Name = name,
                Risk = ToolRisk.Safe,
                Category = ToolCategory.PlanAllowed,
                IsVisible = false,
                LoadPolicy = ToolLoadPolicy.Deferred,
            });
        }

        ToolNames.Initialize(registry);
    }
}
