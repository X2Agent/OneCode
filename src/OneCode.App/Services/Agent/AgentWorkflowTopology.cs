using System.Text;

namespace OneCode.App.Services.Agent;

/// <summary>
/// Agent 任务图工作流定义的纯函数契约：图校验、有效依赖推导、
/// 规范化执行体 ID 与确定性定义哈希。全部成员无实例状态。
/// </summary>
internal static class AgentWorkflowTopology
{
    internal const string DispatcherId = "agent-task-dispatcher-v1";
    internal const string ExecutorIdPrefix = "agent-task-v1-";

    /// <summary>在 MAF 校验执行体绑定与可达性之前校验产品任务图：ID 与提示词非空、依赖存在、无环。</summary>
    internal static void Validate(IReadOnlyList<AgentWorkflowTask> tasks)
    {
        if (tasks.Count == 0)
            throw new InvalidOperationException("At least one task is required.");

        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Id))
                throw new InvalidOperationException("Each task must have a non-empty ID.");
            if (string.IsNullOrWhiteSpace(task.Prompt))
                throw new InvalidOperationException($"Task '{task.Id}' must have a non-empty prompt.");
        }

        var duplicateIds = tasks
            .GroupBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (duplicateIds.Length > 0)
            throw new InvalidOperationException($"Duplicate task IDs detected: {string.Join(", ", duplicateIds)}.");

        var executorIdCollisions = tasks
            .GroupBy(task => NormalizeId(task.Id), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(", ", group.Select(task => task.Id).Order(StringComparer.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (executorIdCollisions.Length > 0)
            throw new InvalidOperationException(
                $"Task IDs produce duplicate normalized executor IDs: {string.Join("; ", executorIdCollisions)}.");

        foreach (var task in tasks)
        {
            if (!Enum.IsDefined(task.ExecutionAccess))
                throw new InvalidOperationException(
                    $"Task '{task.Id}' declares unknown execution access '{task.ExecutionAccess}'.");
        }

        var taskById = tasks.ToDictionary(task => task.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            var duplicateDependencies = task.Dependencies
                .GroupBy(dependency => dependency, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicateDependencies.Length > 0)
                throw new InvalidOperationException(
                    $"Task '{task.Id}' contains duplicate dependencies: {string.Join(", ", duplicateDependencies)}.");

            foreach (var dependency in task.Dependencies)
            {
                if (!taskById.ContainsKey(dependency))
                    throw new InvalidOperationException($"Task '{task.Id}' depends on unknown task '{dependency}'.");
            }
        }

        var visitState = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        foreach (var taskId in taskById.Keys.Order(StringComparer.OrdinalIgnoreCase))
            Visit(taskId);

        void Visit(string taskId)
        {
            if (visitState.TryGetValue(taskId, out var state))
            {
                if (state == 1)
                    throw new InvalidOperationException($"Task graph contains a cycle involving task '{taskId}'.");
                return;
            }

            visitState[taskId] = 1;
            foreach (var dependency in taskById[taskId].Dependencies.Order(StringComparer.OrdinalIgnoreCase))
                Visit(dependency);
            visitState[taskId] = 2;
        }
    }

    /// <summary>推导每个任务的有效依赖：声明依赖排序去重，并保证相邻写任务按稳定拓扑次序串行。</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildEffectiveDependencies(
        IReadOnlyList<AgentWorkflowTask> orderedTasks)
    {
        var dependencies = orderedTasks.ToDictionary(
            task => task.Id,
            task => task.Dependencies.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            StringComparer.OrdinalIgnoreCase);
        var writeTasks = orderedTasks.Where(task => task.ExecutionAccess == AgentTaskExecutionAccess.Write).ToArray();
        for (var index = 1; index < writeTasks.Length; index++)
        {
            var previousWrite = writeTasks[index - 1].Id;
            var currentWrite = writeTasks[index].Id;
            if (!IsReachable(previousWrite, currentWrite, dependencies))
                dependencies[currentWrite].Add(previousWrite);
        }

        return dependencies.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsReachable(
        string ancestor,
        string descendant,
        IReadOnlyDictionary<string, List<string>> dependencies)
    {
        Stack<string> pending = new();
        pending.Push(descendant);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
                continue;
            foreach (var dependency in dependencies[current])
            {
                if (string.Equals(dependency, ancestor, StringComparison.OrdinalIgnoreCase))
                    return true;
                pending.Push(dependency);
            }
        }
        return false;
    }

    /// <summary>计算工作流定义的确定性哈希：写入任务、输出、缓存参数与能力约束的规范化 JSON 视图。</summary>
    internal static string ComputeDefinitionHash(
        IReadOnlyList<AgentWorkflowTask> tasks,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dependencies,
        IReadOnlyList<AgentWorkflowTask> terminalTasks,
        CacheSafeParams? cacheSafeParams,
        ToolCapabilitySet? parentCapabilities)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "agent-task-workflow-definition-v2");
            writer.WriteStartArray("tasks");
            foreach (var task in tasks)
            {
                writer.WriteStartObject();
                writer.WriteString("id", task.Id);
                writer.WriteString("executorId", ExecutorIdPrefix + NormalizeId(task.Id));
                writer.WriteString("agent", task.Agent);
                writer.WriteString("description", task.Description);
                writer.WriteString("prompt", task.Prompt);
                writer.WriteNumber("executionAccess", (int)task.ExecutionAccess);
                writer.WriteBoolean("injectUpstreamResults", task.InjectUpstreamResults);
                writer.WriteStartArray("dependencies");
                foreach (var dependency in dependencies[task.Id])
                    writer.WriteStringValue(dependency);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("outputs");
            foreach (var terminal in terminalTasks)
                writer.WriteStringValue(terminal.Id);
            writer.WriteEndArray();
            writer.WriteString("systemPrompt", cacheSafeParams?.SystemPrompt);
            writer.WriteString("model", cacheSafeParams?.ModelId);
            if (cacheSafeParams?.ThinkingBudget is { } thinkingBudget)
                writer.WriteNumber("thinkingBudget", thinkingBudget);
            else
                writer.WriteNull("thinkingBudget");
            WriteToolNames(writer, "cacheTools", cacheSafeParams?.Tools?.Select(tool => tool.Name));
            WriteCapabilities(writer, "cacheCapabilities", cacheSafeParams?.ToolCapabilities);
            WriteCapabilities(writer, "parentCapabilities", parentCapabilities);
            writer.WriteString("dispatcherId", DispatcherId);
            writer.WriteString("outcomeContract", "AgentTaskOutcome:v1");
            writer.WriteEndObject();
        }

        return WorkflowDefinitionHash.Compute(stream.ToArray());
    }

    private static void WriteToolNames(Utf8JsonWriter writer, string propertyName, IEnumerable<string?>? toolNames)
    {
        writer.WriteStartArray(propertyName);
        foreach (var toolName in (toolNames ?? []).Where(name => name is not null).Order(StringComparer.OrdinalIgnoreCase))
            writer.WriteStringValue(toolName);
        writer.WriteEndArray();
    }

    private static void WriteCapabilities(
        Utf8JsonWriter writer,
        string propertyName,
        ToolCapabilitySet? capabilities)
    {
        writer.WritePropertyName(propertyName);
        if (capabilities is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        WriteToolNames(writer, "allowedToolNames", capabilities.AllowedToolNames);
        writer.WriteNumber("allowedCategories", (int)capabilities.AllowedCategories);
        writer.WriteNumber("maximumRisk", (int)capabilities.MaximumRisk);
        writer.WriteBoolean("allowDynamicActivation", capabilities.AllowDynamicActivation);
        writer.WriteBoolean("allowSubAgents", capabilities.AllowSubAgents);
        writer.WriteEndObject();
    }

    /// <summary>把任务 ID 规范化为执行体 ID 后缀：小写，仅保留字母数字与 - _。</summary>
    internal static string NormalizeId(string id)
    {
        var builder = new StringBuilder(id.Length);
        foreach (var character in id)
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' ? char.ToLowerInvariant(character) : '-');
        return builder.ToString();
    }
}
