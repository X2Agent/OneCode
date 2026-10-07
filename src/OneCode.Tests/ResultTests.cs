using OneCode.Core.Results;

namespace OneCode.Tests;

/// <summary>
/// <see cref="Result{T}"/> 契约测试——PathsHelper.SafeResolve 等生产代码仍在消费该类型，
/// 状态语义（Success/Failure/Deconstruct）必须有回归覆盖。
/// </summary>
public sealed class ResultTests
{
    [Fact]
    public void Success_CarriesValueWithoutError()
    {
        var result = Result<int>.Success(7);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(7);
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Failure_CarriesErrorWithoutValue()
    {
        var result = Result<string>.Failure("disk full");

        result.IsSuccess.Should().BeFalse();
        result.Value.Should().BeNull();
        result.Error.Should().Be("disk full");
    }

    [Fact]
    public void Deconstruct_ExposesAllStateForPatternMatching()
    {
        var (success, value, error) = Result<int>.Success(42);

        success.Should().BeTrue();
        value.Should().Be(42);
        error.Should().BeNull();

        var (failed, failedValue, failedError) = Result<int>.Failure("nope");

        failed.Should().BeFalse();
        failedValue.Should().Be(0);
        failedError.Should().Be("nope");
    }

    [Fact]
    public void Failure_HoldsNullValueForReferenceTypes()
    {
        var result = Result<string>.Failure("not found");

        result.Value.Should().BeNull("Failure 的 Value 恒为 default，消费方必须先检查 IsSuccess");
    }
}
