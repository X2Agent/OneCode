using OneCode.Core.Results;

namespace OneCode.Tests;

public sealed class ResultTests
{
    // Result<T>.GetValueOrThrow — only the genuine guard logic is covered here.
    // The factory/Deconstruct members are pure data plumbing (covered implicitly
    // by callers) and intentionally not duplicated.

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
        // The implementation guards against returning null via `Value is not null`:
        // a "successful" reference-type result holding null still throws.
        var result = Result<string>.Success(null!);
        var act = () => result.GetValueOrThrow();

        act.Should().Throw<InvalidOperationException>().WithMessage("Result is not successful");
    }

    [Fact]
    public void GetValueOrThrow_OnSuccessWithValueTypeZero_ReturnsZero()
    {
        // Value-type branch: 0 is not null, so it is returned as-is and does not throw.
        var result = Result<int>.Success(0);

        result.GetValueOrThrow().Should().Be(0);
    }
}