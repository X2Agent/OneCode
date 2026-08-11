namespace OneCode.Core.PlanMode;

public sealed class PlanConcurrencyException(string message) : InvalidOperationException(message);
public sealed class PlanTransitionException(string message) : InvalidOperationException(message);
