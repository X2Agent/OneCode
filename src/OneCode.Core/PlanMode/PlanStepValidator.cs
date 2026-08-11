namespace OneCode.Core.PlanMode;

public sealed class PlanValidationException(IReadOnlyList<string> errors)
    : InvalidOperationException("Invalid plan steps: " + string.Join("; ", errors))
{
    public PlanValidationException(string error)
        : this([error])
    {
    }

    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class PlanStepValidator
{
    public static void Validate(IReadOnlyList<PlanStepDefinition> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        List<string> errors = [];

        if (steps.Count == 0)
            errors.Add("At least one plan step is required.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Id))
                errors.Add("Every step must have a non-empty ID.");
            else if (!ids.Add(step.Id))
                errors.Add($"Duplicate step ID '{step.Id}'.");

            if (string.IsNullOrWhiteSpace(step.Title))
                errors.Add($"Step '{step.Id}' must have a title.");
            if (string.IsNullOrWhiteSpace(step.Description))
                errors.Add($"Step '{step.Id}' must have a description.");
            if (step.AcceptanceCriteria.Count == 0 || step.AcceptanceCriteria.Any(string.IsNullOrWhiteSpace))
                errors.Add($"Step '{step.Id}' must have non-empty acceptance criteria.");
        }

        foreach (var step in steps)
        {
            foreach (var dependency in step.DependsOn)
            {
                if (!ids.Contains(dependency))
                    errors.Add($"Step '{step.Id}' depends on unknown step '{dependency}'.");
                if (dependency.Equals(step.Id, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"Step '{step.Id}' cannot depend on itself.");
            }
        }

        DetectCycles(steps, errors);

        if (errors.Count > 0)
            throw new PlanValidationException(errors.Distinct().ToArray());
    }

    private static void DetectCycles(IReadOnlyList<PlanStepDefinition> steps, List<string> errors)
    {
        var byId = steps.Where(step => !string.IsNullOrWhiteSpace(step.Id))
            .GroupBy(step => step.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Visit(string id)
        {
            if (visiting.Contains(id))
                return true;
            if (!visited.Add(id) || !byId.TryGetValue(id, out var step))
                return false;

            visiting.Add(id);
            foreach (var dependency in step.DependsOn)
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
                errors.Add($"Plan step dependency graph contains a cycle involving '{id}'.");
                return;
            }
        }
    }
}
