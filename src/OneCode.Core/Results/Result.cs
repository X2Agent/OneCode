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

}
