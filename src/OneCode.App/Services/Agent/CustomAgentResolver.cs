using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>
/// <c>Agent</c> / <c>ParallelAgents</c> 的 <c>agent</c> 参数解析：内置名
/// （general-purpose / Explore / Plan）直通；其余按自定义代理目录解析。
/// </summary>
public static class CustomAgentResolver
{
    public static bool IsBuiltin(string agentName) =>
        agentName is "general-purpose" or "Explore" or "Plan";

    /// <summary>返回解析出的定义；失败时返回显式错误（未知名列出可用清单，坏定义文件原样报错）。</summary>
    /// <remarks>
    /// <paramref name="toolCatalog"/> 用于校验 <c>allowed_tools</c> 中的未知名：拼错的工具名
    /// 会在 fork 侧被「只减不增」语义静默过滤，必须在此显式拒绝，否则用户无从自诊断。
    /// </remarks>
    public static (AgentDefinition? Definition, string? Error) Resolve(
        CustomAgentCatalog? catalog, string agentName, IToolCatalog? toolCatalog = null)
    {
        if (catalog is null || IsBuiltin(agentName))
            return (null, null);

        var errors = catalog.LoadErrors;
        if (errors.Count > 0)
            return (null,
                "Custom agent definitions failed to load:\n" + string.Join("\n", errors));

        var definition = catalog.Find(agentName);
        if (definition is not null)
        {
            if (toolCatalog is not null && definition.AllowedTools is { Count: > 0 })
            {
                var known = toolCatalog.Tools.Select(static tool => tool.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var unknown = definition.AllowedTools.Where(name => !known.Contains(name)).ToList();
                if (unknown.Count > 0)
                    return (null,
                        $"Custom agent '{agentName}' allows unknown tool(s): {string.Join(", ", unknown)}. " +
                        "Check spelling against the available tools; unknown names are silently filtered out of the fork's tool set.");
            }
            return (definition, null);
        }

        var available = catalog.GetAll();
        var names = available.Count > 0
            ? string.Join(", ", available.Select(static d => $"'{d.Name}'"))
            : "(none)";
        return (null,
            $"Unknown agent '{agentName}'. Built-in: general-purpose, Explore, Plan. " +
            $"Custom ({names}) come from .onecode/agents/*.md (project) or ~/.onecode/agents/*.md (user).");
    }
}
