using OneCode.App.Services.Coordinator;

namespace OneCode.Tests;

public sealed class GroupChatActivityTrackerTests
{
    [Fact]
    public void HasSettled_NoActivityEver_ReturnsFalse()
    {
        var tracker = new GroupChatActivityTracker();

        tracker.HasSettled().Should().BeFalse();
    }

    [Fact]
    public void HasSettled_NewActivitySinceLastCheck_ReturnsFalse()
    {
        var tracker = new GroupChatActivityTracker();
        tracker.OnActivity();

        tracker.HasSettled().Should().BeFalse();
        tracker.HasSettled().Should().BeTrue();
    }

    [Fact]
    public void HasSettled_ActivityResumes_AfterSettled_ReturnsFalseAgain()
    {
        var tracker = new GroupChatActivityTracker();
        tracker.OnActivity();
        _ = tracker.HasSettled(); // 第一次收敛

        tracker.OnActivity(); // 讨论恢复

        tracker.HasSettled().Should().BeFalse();
        tracker.HasSettled().Should().BeTrue();
    }
}
