using OneCode.App.Services.Coordinator;
using OneCode.Core.Coordinator;

namespace OneCode.Tests;

public sealed class TeamFileChangeObserverTests
{
    [Fact]
    public void CreateObservedSink_MergesSameFileAndTracksContributors()
    {
        var forwarded = new List<OrchestrationEvent>();
        var (fileChanges, sink) = TeamFileChangeObserver.CreateObservedSink(forwarded.Add);

        sink!(new OrchestrationEvent.FileChanged("alice", "a.cs", ["+1"], ["-1"]));
        sink(new OrchestrationEvent.FileChanged("bob", "a.cs", ["+2"], ["-2"]));
        sink(new OrchestrationEvent.FileChanged("carol", "b.cs", ["+3"], []));
        sink(new OrchestrationEvent.AgentMessage("x", null, "hi"));

        fileChanges.Should().HaveCount(2);
        fileChanges[0].FileName.Should().Be("a.cs");
        fileChanges[0].AddedLines.Should().Equal("+1", "+2");
        fileChanges[0].RemovedLines.Should().Equal("-1", "-2");
        fileChanges[0].Contributors.Should().Equal("alice", "bob");
        fileChanges[1].FileName.Should().Be("b.cs");
        fileChanges[1].Contributors.Should().Equal("carol");
        forwarded.Should().HaveCount(4);
    }

    [Fact]
    public void CreateObservedSink_NullOuterSink_StillAccumulates()
    {
        var (fileChanges, sink) = TeamFileChangeObserver.CreateObservedSink(null);
        sink!(new OrchestrationEvent.FileChanged("a", "x.cs", ["l"], []));
        fileChanges.Should().ContainSingle(c => c.FileName == "x.cs");
    }
}
