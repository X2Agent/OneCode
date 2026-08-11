namespace OneCode.Core.Results;

/// <summary>
/// Generic result type for operations that can succeed or fail.
/// Provides a type-safe alternative to exceptions for expected failures.
/// </summary>
public sealed class Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? Error { get; }

    private Result(bool isSuccess, T? value, string? error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    /// <summary>
    /// Create a successful result.
    /// </summary>
    public static Result<T> Success(T value) => new(true, value, null);

    /// <summary>
    /// Create a failure result.
    /// </summary>
    public static Result<T> Failure(string error) => new(false, default, error);

    /// <summary>
    /// Deconstruct for pattern matching.
    /// </summary>
    public void Deconstruct(out bool isSuccess, out T? value, out string? error)
    {
        isSuccess = IsSuccess;
        value = Value;
        error = Error;
    }

    /// <summary>
    /// Get the value or throw if failure.
    /// </summary>
    public T GetValueOrThrow() => IsSuccess && Value is not null
        ? Value
        : throw new InvalidOperationException(Error ?? "Result is not successful");
}

/// <summary>
/// Non-generic result for operations without a return value.
/// </summary>
public sealed class Result
{
    public bool IsSuccess { get; }
    public string? Error { get; }

    private Result(bool isSuccess, string? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public static Result Success() => new(true, null);
    public static Result Failure(string error) => new(false, error);
}
