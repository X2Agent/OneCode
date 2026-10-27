#pragma warning disable MAAI001 // MAF 实验性 API（AIContextProvider.InvokingContext 构造）：升级 MAF 时按升级复查清单回归
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Build;

namespace OneCode.App.Services.Agent;

/// <summary>
/// 把已批准计划的步骤预置进 Harness <see cref="TodoProvider"/> 会话状态：步骤进度只有一个事实源——
/// agent 自己的待办清单（<c>todos_complete</c> 勾选），不再要求模型向另一套机器记账工具上报。
/// </summary>
/// <remarks>
/// 只在清单为空时预置：同一会话恢复续跑时保留已勾选的进度，不被重置。
/// 标题带步骤 id 前缀（<c>"{stepId}: {title}"</c>），PlanCard 据此把待办映射回计划步骤。
/// </remarks>
public static class PlanTodoSeeder
{
    private const string TitleSeparator = ": ";
    private const string AddToolName = "todos_add";
    private const string CompleteToolName = "todos_complete";

    /// <summary>预置待办。返回 true 表示本次实际写入；清单非空或 profile 未启用 Todo 返回 false。</summary>
    public static async Task<bool> SeedAsync(
        AIAgent agent,
        AgentSession session,
        IReadOnlyList<BuildPlanTask> tasks,
        CancellationToken ct = default)
    {
        if (tasks.Count == 0 || agent.GetService<TodoProvider>() is not { } provider)
            return false;
        if ((await provider.GetAllTodosAsync(session, ct).ConfigureAwait(false)).Count > 0)
            return false;

        // TodoState 是 MAF 内部类型，不能直接写 StateBag；走 provider 自己注入的 todos_add 工具——
        // 与模型调用同一条公开契约，状态格式与序列化由框架负责。
        var payload = new
        {
            todos = tasks.Select(task => new { title = FormatTitle(task), description = FormatDescription(task) }),
        };
        await InvokeTodoToolAsync(provider, agent, session, AddToolName, payload, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 受控计划 Build 正常完成且验证通过后的收尾补账：将当前会话中剩余未勾选的待办一并标记为完成。
    /// 与 <c>BuildRunCoordinator.MarkExecutionEvidencePassed</c> 对齐——构建成败由产物与验证决定，
    /// 模型在收尾轮漏调 <c>todos_complete</c> 不应让已完成的计划在会话状态与待办面板中残留未完成项。
    /// 返回本次补账勾选的条目数。
    /// </summary>
    public static async Task<int> CompleteRemainingAsync(
        AIAgent agent,
        AgentSession session,
        CancellationToken ct = default)
    {
        if (agent.GetService<TodoProvider>() is not { } provider)
            return 0;

        var remaining = await provider.GetRemainingTodosAsync(session, ct).ConfigureAwait(false);
        if (remaining.Count == 0)
            return 0;

        var payload = new
        {
            items = remaining.Select(item => new { id = item.Id, reason = "completed on build closeout" }),
        };
        await InvokeTodoToolAsync(provider, agent, session, CompleteToolName, payload, ct).ConfigureAwait(false);
        return remaining.Count;
    }

    private static async Task InvokeTodoToolAsync(
        TodoProvider provider,
        AIAgent agent,
        AgentSession session,
        string toolName,
        object payload,
        CancellationToken ct)
    {
        var context = new AIContextProvider.InvokingContext(agent, session, new AIContext());
        var aiContext = await provider.InvokingAsync(context, ct).ConfigureAwait(false);
        var tool = aiContext.Tools?.OfType<AIFunction>().FirstOrDefault(t => t.Name == toolName)
            ?? throw new InvalidOperationException($"TodoProvider did not expose '{toolName}'.");

        var arguments = new AIFunctionArguments();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        foreach (var property in document.RootElement.EnumerateObject())
            arguments[property.Name] = property.Value.Clone();
        _ = await tool.InvokeAsync(arguments, ct).ConfigureAwait(false);
    }

    /// <summary>待办标题：步骤 id 前缀 + 步骤标题。</summary>
    public static string FormatTitle(BuildPlanTask task) => $"{task.Id}{TitleSeparator}{task.Title}";

    /// <summary>从待办标题还原步骤 id；非计划预置的待办（模型自加）返回 null。</summary>
    public static string? TryParseStepId(string title)
    {
        var separator = title.IndexOf(TitleSeparator, StringComparison.Ordinal);
        return separator > 0 ? title[..separator] : null;
    }

    /// <summary>
    /// 待办是否是为这些步骤预置的：前缀 + 标题都要对得上。仅看前缀会把模型自加的
    /// 「Note: …」之类待办误判成计划步骤。
    /// </summary>
    public static bool IsSeededFor(string todoTitle, IReadOnlyCollection<string> stepTitles)
    {
        var separator = todoTitle.IndexOf(TitleSeparator, StringComparison.Ordinal);
        return separator > 0 && stepTitles.Contains(todoTitle[(separator + TitleSeparator.Length)..]);
    }

    private static string FormatDescription(BuildPlanTask task) =>
        task.AcceptanceCriteria.Count == 0
            ? task.Description
            : $"{task.Description}\nAcceptance: {string.Join("; ", task.AcceptanceCriteria)}";
}
