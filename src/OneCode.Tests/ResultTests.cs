using OneCode.Core.Results;

namespace OneCode.Tests;

public sealed class ResultTests
{
    // Result<T> · Success factory

    [Fact]
    public void Success_SetsIsSuccessTrue_AndValue_AndErrorNull()
    {
        var result = Result<int>.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Success_WithNullReferenceType_ValueIsNull()
    {
        var result = Result<string>.Success(null!);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
        result.Error.Should().BeNull();
    }

    // Result<T> · Failure factory

    [Fact]
    public void Failure_SetsIsSuccessFalse_AndError_AndValueDefault()
    {
        // For unconstrained T, T? is T with nullable annotation — default(int) is 0,
        // NOT null. Only reference-type T produces a null Value on Failure.
        var result = Result<int>.Failure("boom");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("boom");
        result.Value.Should().Be(0);
    }

    [Fact]
    public void Failure_WithReferenceType_ValueIsNull()
    {
        var result = Result<string>.Failure("missing");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("missing");
        result.Value.Should().BeNull();
    }

    // Result<T> · GetValueOrThrow

    [Fact]
    public void GetValueOrThrow_OnSuccess_ReturnsValue()
    {
        var result = Result<int>.Success(7);
        result.GetValueOrThrow().Should().Be(7);
    }

    [Fact]
    public void GetValueOrThrow_OnFailure_ThrowsWithError()
    {
        var result = Result<int>.Failure("disk full");
        var act = () => result.GetValueOrThrow();

        act.Should().Throw<InvalidOperationException>().WithMessage("disk full");
    }

    [Fact]
    public void GetValueOrThrow_OnSuccessWithNullValue_Throws()
    {
        // Subtle but important: a "successful" reference-type result with a null
        // value still throws because the implementation guards against returning
        // null via `Value is not null`. For value types, default(int)=0 is not
        // null, so a successful Result<int>.Success(0) does NOT throw.
        var result = Result<string>.Success(null!);
        var act = () => result.GetValueOrThrow();

        act.Should().Throw<InvalidOperationException>().WithMessage("Result is not successful");
    }

    [Fact]
    public void GetValueOrThrow_OnSuccessWithValueTypeZero_ReturnsZero()
    {
        // Confirms the value-type branch: 0 is not null, so it is returned as-is.
        var result = Result<int>.Success(0);
        result.GetValueOrThrow().Should().Be(0);
    }

    // Result<T> · Deconstruct

    [Fact]
    public void Deconstruct_Success_ReturnsTrueValueNullError()
    {
        var result = Result<int>.Success(99);
        var (isSuccess, value, error) = result;

        isSuccess.Should().BeTrue();
        value.Should().Be(99);
        error.Should().BeNull();
    }

    [Fact]
    public void Deconstruct_Failure_ReturnsFalseDefaultValueError()
    {
        var result = Result<int>.Failure("nope");
        var (isSuccess, value, error) = result;

        isSuccess.Should().BeFalse();
        value.Should().Be(0);
        error.Should().Be("nope");
    }

    // Non-generic Result

    [Fact]
    public void NonGeneric_Success_SetsIsSuccessTrue_AndErrorNull()
    {
        var result = Result.Success();

        result.IsSuccess.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Fact]
    public void NonGeneric_Failure_SetsIsSuccessFalse_AndError()
    {
        var result = Result.Failure("denied");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("denied");
    }
}
