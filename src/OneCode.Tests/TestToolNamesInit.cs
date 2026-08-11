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
                                     "SymbolSearch", "Lsp", "FindReferences", "BackgroundWait",
                                     "CronList", "AskUserQuestion", "AskUserQuestions" })
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
        foreach (var name in new[] { "SavePlan", "SubmitPlan", "Task", "Agent", "ParallelAgents" })
        {
            registry.Register(new ToolMetadata
            {
                Name = name,
                Risk = ToolRisk.Safe,
                Category = ToolCategory.PlanAllowed,
            });
        }
        // User-question tools are both ReadOnly and PlanAllowed.
        foreach (var name in new[] { "AskUserQuestion", "AskUserQuestions" })
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
        registry.Register(new ToolMetadata { Name = "PowerShell", Risk = ToolRisk.Dynamic });

        ToolNames.Initialize(registry);
    }
}
