using OneCode.Core.Coordinator;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// Team 任务图工作流定义的纯函数契约：图校验、有效依赖推导、
/// 规范化执行体 ID 与确定性定义哈希。全部成员无实例状态。
/// </summary>
internal static class TeamWorkflowTopology
{
    internal const string DispatcherId = "team-task-dispatcher-v1";
    internal const string ExecutorIdPrefix = "team-task-v1-";

    /// <summary>校验已批准的 Team 任务图：非空、ID 唯一、执行体 ID 规范化后不冲突、工具策略已知、依赖存在且无环。</summary>
    internal static void Validate(IReadOnlyList<TeamTaskDefinition> tasks)
    {
        if (tasks.Count == 0)
            throw new InvalidOperationException("Team task graph requires at least one task.");

        var duplicateIds = tasks
            .GroupBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (duplicateIds.Length > 0)
            throw new InvalidOperationException($"Duplicate Team task IDs detected: {string.Join(", ", duplicateIds)}.");

        var executorIdCollisions = tasks
            .GroupBy(task => NormalizeId(task.Id), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(", ", group.Select(task => task.Id).Order(StringComparer.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (executorIdCollisions.Length > 0)
        {
            throw new InvalidOperationException(
                $"Team task IDs produce duplicate normalized executor IDs: {string.Join("; ", executorIdCollisions)}.");
        }

        var taskById = tasks.ToDictionary(task => task.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Id))
                throw new InvalidOperationException("Each Team task must have a non-empty ID.");
            if (!Enum.IsDefined(task.ToolPolicy))
                throw new InvalidOperationException($"Team task '{task.Id}' declares unknown tool policy '{task.ToolPolicy}'.");
            foreach (var dependency in task.DependsOn)
            {
                if (!taskById.ContainsKey(dependency))
                    throw new InvalidOperationException($"Team task '{task.Id}' depends on unknown task '{dependency}'.");
            }
        }

        _ = WorkflowTopology.KahnOrder(
            tasks, task => task.Id, task => task.DependsOn,
            "Team task graph contains a dependency cycle.");
    }

    /// <summary>推导每个任务的有效依赖：声明依赖排序去重，并保证相邻写任务按稳定拓扑次序串行。</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildEffectiveDependencies(
        IReadOnlyList<TeamTaskDefinition> orderedTasks)
    {
        var dependencies = orderedTasks.ToDictionary(
            task => task.Id,
            task => task.DependsOn.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            StringComparer.OrdinalIgnoreCase);
        var writeTasks = orderedTasks.Where(task => task.ToolPolicy == TeamToolPolicy.WriteAllowed).ToArray();
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

    /// <summary>计算工作流定义的确定性哈希：写入团队、模式、模型、成员、任务与门禁的规范化 JSON 视图。</summary>
    internal static string ComputeDefinitionHash(
        TeamRun run,
        TeamConfig config,
        string modelId,
        IReadOnlyList<TeamTaskDefinition> tasks,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dependencies,
        IReadOnlyList<TeamTaskDefinition> terminalTasks,
        JsonSerializerOptions? serializerOptions)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "team-task-workflow-definition-v1");
            // 定义哈希标识“工作流定义”，不依赖具体 Run 实例（runId 已由 Registration.RunId 承载），
            // 保证等价定义（含输入任务顺序置换）产生一致哈希。
            writer.WriteString("team", run.TeamName);
            writer.WriteNumber("mode", (int)config.Mode);
            writer.WriteString("modelId", modelId);
            writer.WriteNumber("maxTurns", config.MaxTurns);
            writer.WriteStartArray("members");
            foreach (var member in config.Members.OrderBy(member => member.AgentId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("agentId", member.AgentId);
                writer.WriteString("role", member.Role);
                writer.WriteString("systemPrompt", member.SystemPrompt);
                writer.WriteStartArray("allowedTools");
                foreach (var tool in (member.AllowedTools ?? []).Order(StringComparer.OrdinalIgnoreCase))
                    writer.WriteStringValue(tool);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("tasks");
            foreach (var task in tasks)
            {
                writer.WriteStartObject();
                writer.WriteString("id", task.Id);
                writer.WriteString("executorId", ExecutorIdPrefix + NormalizeId(task.Id));
                writer.WriteNumber("kind", (int)task.Kind);
                writer.WriteString("assigneeRole", task.AssigneeRole);
                writer.WriteNumber("toolPolicy", (int)task.ToolPolicy);
                writer.WriteBoolean("required", task.Required);
                writer.WriteStartArray("requiredTools");
                foreach (var tool in (task.RequiredTools ?? []).Order(StringComparer.OrdinalIgnoreCase))
                    writer.WriteStringValue(tool);
                writer.WriteEndArray();
                writer.WriteStartArray("allowedPaths");
                foreach (var path in (task.AllowedPaths ?? []).Order(StringComparer.OrdinalIgnoreCase))
                    writer.WriteStringValue(path);
                writer.WriteEndArray();
                writer.WriteStartArray("requiredGates");
                foreach (var gate in (task.RequiredGates ?? []).Order(StringComparer.OrdinalIgnoreCase))
                    writer.WriteStringValue(gate);
                writer.WriteEndArray();
                writer.WriteString("title", task.Title);
                writer.WriteStartArray("acceptanceCriteria");
                foreach (var criterion in task.AcceptanceCriteria.Order(StringComparer.Ordinal))
                    writer.WriteStringValue(criterion);
                writer.WriteEndArray();
                writer.WriteStartArray("dependencies");
                foreach (var dependency in dependencies[task.Id])
                    writer.WriteStringValue(dependency);
                writer.WriteEndArray();
                writer.WriteStartArray("expectedOutputs");
                foreach (var output in (task.ExpectedOutputs ?? []).Order(StringComparer.Ordinal))
                    writer.WriteStringValue(output);
                writer.WriteEndArray();
                writer.WriteNumber("maxAttempts", task.MaxAttempts);
                if (task.RetryPolicy is { } policy)
                {
                    writer.WriteStartObject("retryPolicy");
                    writer.WriteNumber("maxAttempts", policy.MaxAttempts);
                    writer.WriteString("initialDelay", policy.InitialDelay.ToString());
                    writer.WriteString("maxDelay", policy.MaxDelay.ToString());
                    writer.WriteNumber("backoffMultiplier", policy.BackoffMultiplier);
                    writer.WriteStartArray("retryableErrorFingerprints");
                    foreach (var fingerprint in (policy.RetryableErrorFingerprints ?? []).Order(StringComparer.Ordinal))
                        writer.WriteStringValue(fingerprint);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteNull("retryPolicy");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("requiredGates");
            foreach (var gate in (run.Plan?.RequiredGates ?? [])
                .OrderBy(gate => gate.Id, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", gate.Id);
                writer.WriteNumber("kind", (int)gate.Kind);
                writer.WriteBoolean("required", gate.Required);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("outputs");
            foreach (var terminal in terminalTasks)
                writer.WriteStringValue(ExecutorIdPrefix + NormalizeId(terminal.Id));
            writer.WriteEndArray();
            writer.WriteString("contract", "TeamTaskOutcome:v1");
            writer.WriteString(
                "serializerOptions",
                WorkflowDefinitionHash.DescribeSerializerOptions(serializerOptions));
            writer.WriteEndObject();
        }

        return WorkflowDefinitionHash.Compute(stream.ToArray());
    }

    /// <summary>把任务 ID 规范化为执行体 ID 后缀：小写、非字母数字折叠为连字符、连续连字符合并。</summary>
    internal static string NormalizeId(string id)
    {
        var chars = id.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        var normalized = new string(chars);
        while (normalized.Contains("--", StringComparison.Ordinal))
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        return normalized.Trim('-');
    }
}
