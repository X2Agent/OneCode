namespace OneCode.Core.Workflows;

/// <summary>
/// 工作流步骤图拓扑排序共享内核——收敛 Build/Plan/Team/Agent 各自重复的排序实现。
/// 提供两种既有语义：DFS 后序（Build/Plan 家族：输入序遍历根、依赖声明序递归）与
/// Kahn 字典序（Team/Agent 家族：就绪集按 Id OrdinalIgnoreCase 出队）。
/// 两种算法对同一输入产生不同顺序，不可互替——调用方按原语义选择。
/// </summary>
public static class WorkflowTopology
{
    /// <summary>
    /// DFS 后序稳定拓扑序。缺失依赖不抛异常——跳过并收集进结果，由调用方按
    /// 各自领域语义（Plan 的 PlanTransitionException / Build 的静默容错）处置；
    /// 环依赖与原实现一致：visited 守卫保证终止，环上节点仍按后序产出。
    /// </summary>
    /// <returns>排序结果；<see cref="DepthFirstOrderResult{T}.MissingDependencies"/> 为
    /// 声明了但不在节点集内的依赖 Id，按遍历遇到顺序排列。</returns>
    public static DepthFirstOrderResult<T> DepthFirstOrder<T>(
        IReadOnlyList<T> nodes,
        Func<T, string> idOf,
        Func<T, IReadOnlyList<string>> dependenciesOf)
    {
        var byId = nodes.ToDictionary(idOf, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<T>(nodes.Count);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        void Visit(T node)
        {
            if (!visited.Add(idOf(node)))
                return;
            foreach (var dependency in dependenciesOf(node))
            {
                if (byId.TryGetValue(dependency, out var parent))
                    Visit(parent);
                else
                    missing.Add(dependency);
            }
            ordered.Add(node);
        }

        foreach (var node in nodes)
            Visit(node);
        return new DepthFirstOrderResult<T>(ordered, missing);
    }

    /// <summary>
    /// Kahn 字典序拓扑序（Team/Agent 家族语义）：就绪集与后继遍历均按 Id
    /// OrdinalIgnoreCase 排序，保证同图同序。依赖 Id 不在节点集内时与原实现一致
    /// 抛 <see cref="KeyNotFoundException"/>；存在环时抛
    /// <see cref="InvalidOperationException"/>，消息由 <paramref name="cycleExceptionMessage"/> 提供。
    /// </summary>
    public static IReadOnlyList<T> KahnOrder<T>(
        IReadOnlyList<T> nodes,
        Func<T, string> idOf,
        Func<T, IReadOnlyList<string>> dependenciesOf,
        string cycleExceptionMessage)
    {
        var taskById = nodes.ToDictionary(idOf, StringComparer.OrdinalIgnoreCase);
        var remainingDependencies = nodes.ToDictionary(
            idOf, node => dependenciesOf(node).Count, StringComparer.OrdinalIgnoreCase);
        var dependents = nodes.ToDictionary(
            idOf, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            foreach (var dependency in dependenciesOf(node))
                dependents[dependency].Add(idOf(node));
        }

        var ready = new SortedSet<string>(
            remainingDependencies.Where(pair => pair.Value == 0).Select(pair => pair.Key),
            StringComparer.OrdinalIgnoreCase);
        var ordered = new List<T>(nodes.Count);
        while (ready.Count > 0)
        {
            var nodeId = ready.Min!;
            ready.Remove(nodeId);
            ordered.Add(taskById[nodeId]);
            foreach (var dependent in dependents[nodeId].Order(StringComparer.OrdinalIgnoreCase))
            {
                remainingDependencies[dependent]--;
                if (remainingDependencies[dependent] == 0)
                    ready.Add(dependent);
            }
        }

        if (ordered.Count != nodes.Count)
            throw new InvalidOperationException(cycleExceptionMessage);
        return ordered;
    }
}

/// <summary><see cref="WorkflowTopology.DepthFirstOrder{T}"/> 的结果。</summary>
public sealed record DepthFirstOrderResult<T>(
    IReadOnlyList<T> Ordered,
    IReadOnlyList<string> MissingDependencies);
