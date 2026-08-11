namespace OneCode.Core.Domain;

/// <summary>契约验证结果的抽象基类。</summary>
public abstract record ContractResult
{
    public static ContractResult Passed { get; } = new ContractPassed();

    public static ContractResult Skipped(string reason) => new ContractSkipped(reason);

    public static ContractResult Failed(string description, string? details = null)
        => new ContractFailed(description, details);
}

/// <summary>验证通过。</summary>
public sealed record ContractPassed : ContractResult;

/// <summary>验证跳过（如条件不适用）。</summary>
public sealed record ContractSkipped(string Reason) : ContractResult;

/// <summary>验证失败。</summary>
public sealed record ContractFailed(string Description, string? Details) : ContractResult;
