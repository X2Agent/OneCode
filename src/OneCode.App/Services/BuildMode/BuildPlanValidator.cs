using OneCode.Core.Build;

namespace OneCode.App.Services.BuildMode;

public sealed class BuildPlanValidationException(IReadOnlyList<string> errors)
    : InvalidOperationException("Invalid Build plan: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Deterministic structural validation for persisted Build task graphs.
/// </summary>
public static class BuildPlanValidator
{
    public static void Validate(BuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(plan.Summary))
            errors.Add("The plan summary is required.");
        if (plan.Tasks.Count == 0)
            errors.Add("At least one Build task is required.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in plan.Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Id))
                errors.Add("Every Build task must have a non-empty ID.");
            else if (!ids.Add(task.Id))
                errors.Add($"Duplicate Build task ID '{task.Id}'.");

            if (string.IsNullOrWhiteSpace(task.Title))
                errors.Add($"Build task '{task.Id}' must have a title.");
            if (string.IsNullOrWhiteSpace(task.Description))
                errors.Add($"Build task '{task.Id}' must have a description.");
            if (task.AcceptanceCriteria.Count == 0
                || task.AcceptanceCriteria.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"Build task '{task.Id}' must have non-empty acceptance criteria.");
            }
        }

        foreach (var task in plan.Tasks)
        {
            foreach (var dependency in task.DependsOn)
            {
                if (!ids.Contains(dependency))
                    errors.Add($"Build task '{task.Id}' depends on unknown task '{dependency}'.");
                if (dependency.Equals(task.Id, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"Build task '{task.Id}' cannot depend on itself.");
            }
        }

        DetectCycles(plan.Tasks, errors);
        if (errors.Count > 0)
            throw new BuildPlanValidationException(errors.Distinct().ToArray());
    }

    private static void DetectCycles(
        IReadOnlyList<BuildPlanTask> tasks,
        List<string> errors)
    {
        var byId = tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.Id))
            .GroupBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Visit(string id)
        {
            if (visiting.Contains(id))
                return true;
            if (!visited.Add(id) || !byId.TryGetValue(id, out var task))
                return false;

            visiting.Add(id);
            foreach (var dependency in task.DependsOn)
            {
                if (Visit(dependency))
                    return true;
            }
            visiting.Remove(id);
            return false;
        }

        foreach (var id in byId.Keys)
        {
            visiting.Clear();
            if (Visit(id))
            {
                errors.Add($"Build task dependency graph contains a cycle involving '{id}'.");
                return;
            }
        }
    }
}
