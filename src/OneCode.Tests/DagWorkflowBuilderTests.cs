using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.Core.Domain;
using OneCode.Core.Errors;
using OneCode.Core.Tools;

namespace OneCode.Tests;

/// <summary>Validates the MAF WorkflowBuilder implementation used for dynamic agent task graphs.</summary>
public sealed class DagWorkflowBuilderTests
{
    [Fact]
    public async Task RunAsync_DiamondGraph_AllTasksExecute()
    {
        var callOrder = new List<string>();
        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<AgentRunRequest>();
                lock (callOrder)
                    callOrder.Add(request.Description!);
                return System.Threading.Tasks.Task.FromResult(new AgentRunResult(
                    "agent",
                    SessionId.NewId(),
                    $"{request.Description}-done",
                    1,
                    false));
            });

        var result = await RunAsync(runner, [
            Task("root"),
            Task("left", ["root"]),
            Task("right", ["root"]),
            Task("sink", ["left", "right"]),
        ]);

        result.TaskOutcomes.Should().HaveCount(4);
        result.AllSucceeded.Should().BeTrue();
        callOrder.IndexOf("root").Should().BeLessThan(callOrder.IndexOf("left"));
        callOrder.IndexOf("root").Should().BeLessThan(callOrder.IndexOf("right"));
        callOrder.IndexOf("left").Should().BeLessThan(callOrder.IndexOf("sink"));
        callOrder.IndexOf("right").Should().BeLessThan(callOrder.IndexOf("sink"));
    }

    [Fact]
    public async Task RunAsync_MultiDependency_InjectsAllUpstreamResults()
    {
        string? downstreamPrompt = null;
        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<AgentRunRequest>();
                if (request.Description == "down")
                    downstreamPrompt = request.Prompt;
                return System.Threading.Tasks.Task.FromResult(new AgentRunResult(
                    "agent",
                    SessionId.NewId(),
                    $"output-{request.Description}",
                    1,
                    false));
            });

        var result = await RunAsync(runner, [
            Task("up1"),
            Task("up2"),
            Task("down", ["up1", "up2"]),
        ]);

        result.AllSucceeded.Should().BeTrue();
        downstreamPrompt.Should().Contain("output-up1");
        downstreamPrompt.Should().Contain("output-up2");
        downstreamPrompt.Should().Contain("prompt-down");
    }

    [Fact]
    public async Task RunAsync_UpstreamFails_FanInProducesBlockedOutcomeWithoutHanging()
    {
        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<AgentRunRequest>();
                if (request.Description == "up2")
                    return System.Threading.Tasks.Task.FromResult(new AgentRunResult(
                        "agent",
                        SessionId.NewId(),
                        null,
                        1,
                        false,
                        new AgentProblemDetails("rate-limit", "Rate limit", 429, "Too many requests", "trace")));
                return System.Threading.Tasks.Task.FromResult(new AgentRunResult("agent", SessionId.NewId(), "ok", 1, false));
            });

        var result = await RunAsync(runner, [
            Task("up1"),
            Task("up2"),
            Task("down", ["up1", "up2"]),
        ]);

        result.TaskOutcomes.Single(outcome => outcome.TaskId == "up2").Status.Should().Be(AgentTaskOutcomeStatus.Failed);
        var blocked = result.TaskOutcomes.Single(outcome => outcome.TaskId == "down");
        blocked.Status.Should().Be(AgentTaskOutcomeStatus.Blocked);
        blocked.Error.Should().Contain("up2");
        await runner.Received(2).RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_LinearGraph_RespectsSuperstepOrder()
    {
        var callOrder = new List<string>();
        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<AgentRunRequest>();
                callOrder.Add(request.Description!);
                return System.Threading.Tasks.Task.FromResult(new AgentRunResult("agent", SessionId.NewId(), "done", 1, false));
            });

        var result = await RunAsync(runner, [
            Task("a"),
            Task("b", ["a"]),
            Task("c", ["b"]),
            Task("d", ["c"]),
        ]);

        result.AllSucceeded.Should().BeTrue();
        callOrder.Should().Equal("a", "b", "c", "d");
    }

    [Fact]
    public async Task RunAsync_IndependentReadOnlyTasks_OverlapExecution()
    {
        var started = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                if (Interlocked.Increment(ref started) == 2)
                    bothStarted.TrySetResult();
                await release.Task.WaitAsync(callInfo.Arg<CancellationToken>());
                return new AgentRunResult("agent", SessionId.NewId(), "done", 1, false);
            });

        var run = RunAsync(runner, [Task("a"), Task("b")]);
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        release.TrySetResult();
        var result = await run;

        result.AllSucceeded.Should().BeTrue();
        Volatile.Read(ref started).Should().Be(2);
    }

    [Fact]
    public void CompileDefinition_InputPermutation_ProducesStableTopologyAndHash()
    {
        var compiler = CreateCompiler();
        AgentWorkflowTask[] first =
        [
            Task("sink", ["left", "right"]),
            Task("write-z", ["root"], AgentTaskExecutionAccess.Write),
            Task("root"),
            Task("right", ["root"]),
            Task("write-a", ["root"], AgentTaskExecutionAccess.Write),
            Task("left", ["root"]),
        ];
        AgentWorkflowTask[] second = [first[2], first[5], first[4], first[3], first[1], first[0]];

        var firstDefinition = compiler.CompileDefinition(first, null, null);
        var secondDefinition = compiler.CompileDefinition(second, null, null);

        firstDefinition.DefinitionHash.Should().Be(secondDefinition.DefinitionHash);
        firstDefinition.Tasks.Select(task => task.Id).Should().Equal(secondDefinition.Tasks.Select(task => task.Id));
        firstDefinition.TerminalTaskIds.Should().Equal(secondDefinition.TerminalTaskIds);
        SerializeEdges(firstDefinition).Should().Equal(SerializeEdges(secondDefinition));
        firstDefinition.EffectiveDependencies["write-z"].Should().Contain("write-a");
    }

    [Fact]
    public void CompileDefinition_ManyInputPermutations_ProduceIdenticalDefinition()
    {
        var compiler = CreateCompiler();
        AgentWorkflowTask[] tasks =
        [
            Task("root-a"),
            Task("root-b"),
            Task("read-a", ["root-a"]),
            Task("write-a", ["root-a"], AgentTaskExecutionAccess.Write),
            Task("read-b", ["root-b"]),
            Task("write-b", ["root-b"], AgentTaskExecutionAccess.Write),
            Task("sink", ["read-a", "write-a", "read-b", "write-b"]),
        ];
        var baseline = compiler.CompileDefinition(tasks, null, null);
        var random = new Random(0x5A02);

        for (var iteration = 0; iteration < 100; iteration++)
        {
            var permutation = tasks.OrderBy(_ => random.Next()).ToArray();
            var compiled = compiler.CompileDefinition(permutation, null, null);

            compiled.DefinitionHash.Should().Be(baseline.DefinitionHash);
            compiled.Tasks.Select(task => task.Id).Should().Equal(baseline.Tasks.Select(task => task.Id));
            compiled.TerminalTaskIds.Should().Equal(baseline.TerminalTaskIds);
            SerializeEdges(compiled).Should().Equal(SerializeEdges(baseline));
        }
    }

    [Fact]
    public void CompileDefinition_RandomAcyclicGraphs_PreserveDependenciesAndSerializeWrites()
    {
        var compiler = CreateCompiler();
        var random = new Random(0x5A06);
        for (var graphIndex = 0; graphIndex < 50; graphIndex++)
        {
            var tasks = CreateRandomDag(random, taskCount: 12);
            var definition = compiler.CompileDefinition(tasks, null, null);
            var levels = ComputeLevels(definition);

            foreach (var task in tasks)
            {
                foreach (var dependency in task.Dependencies)
                {
                    definition.EffectiveDependencies[task.Id]
                        .Should().Contain(dependency, "implicit write edges must not weaken approved dependencies");
                }
            }
            levels.Should().OnlyContain(level => level.Count(taskId =>
                tasks.Single(task => task.Id == taskId).ExecutionAccess == AgentTaskExecutionAccess.Write) <= 1);

            var writeTasks = definition.Tasks
                .Where(task => task.ExecutionAccess == AgentTaskExecutionAccess.Write)
                .Select(task => task.Id)
                .ToArray();
            for (var index = 1; index < writeTasks.Length; index++)
                IsReachable(writeTasks[index - 1], writeTasks[index], definition.EffectiveDependencies).Should().BeTrue();
        }
    }

    [Fact]
    public void CompileDefinition_DifferentCultures_ProducesSameHash()
    {
        var compiler = CreateCompiler();
        AgentWorkflowTask[] tasks =
        [
            Task("root"),
            Task("writer", ["root"], AgentTaskExecutionAccess.Write),
        ];
        var parameters = new CacheSafeParams { SystemPrompt = "system", ModelId = "model", ThinkingBudget = 1234 };
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var turkishHash = compiler.CompileDefinition(tasks, parameters, null).DefinitionHash;

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var frenchHash = compiler.CompileDefinition(tasks, parameters, null).DefinitionHash;

            turkishHash.Should().Be(frenchHash);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void CompileDefinition_ReverseNamedWriteDependency_PreservesApprovedOrderWithoutCycle()
    {
        var definition = CreateCompiler().CompileDefinition(
            [
                Task("a-write", ["z-write"], AgentTaskExecutionAccess.Write),
                Task("z-write", access: AgentTaskExecutionAccess.Write),
            ],
            null,
            null);

        definition.Tasks.Select(task => task.Id).Should().Equal("z-write", "a-write");
        definition.EffectiveDependencies["a-write"].Should().Equal("z-write");
        definition.EffectiveDependencies["z-write"].Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ImplicitWritePredecessorFails_BlocksLaterWrite()
    {
        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<AgentRunRequest>();
                return System.Threading.Tasks.Task.FromResult(request.Description == "write-a"
                    ? new AgentRunResult(
                        "agent",
                        SessionId.NewId(),
                        null,
                        1,
                        false,
                        new AgentProblemDetails("write-failed", "Write failed", 500, "write failed", "trace"))
                    : new AgentRunResult("agent", SessionId.NewId(), "done", 1, false));
            });

        var result = await RunAsync(runner,
        [
            Task("write-b", access: AgentTaskExecutionAccess.Write),
            Task("write-a", access: AgentTaskExecutionAccess.Write),
        ]);

        result.TaskOutcomes.Single(outcome => outcome.TaskId == "write-a").Status
            .Should().Be(AgentTaskOutcomeStatus.Failed);
        result.TaskOutcomes.Single(outcome => outcome.TaskId == "write-b").Status
            .Should().Be(AgentTaskOutcomeStatus.Blocked);
        await runner.Received(1).RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void CompileDefinition_DefinitionInputsChange_HashChanges()
    {
        var compiler = CreateCompiler();
        var baselineTask = Task("a");
        var baselineParams = new CacheSafeParams { SystemPrompt = "system-a", ModelId = "model-a", ThinkingBudget = 1 };
        var baselineCapabilities = ToolCapabilitySet.CreateUnrestricted(["Read"]);
        var baseline = compiler.CompileDefinition([baselineTask], baselineParams, baselineCapabilities).DefinitionHash;

        compiler.CompileDefinition([baselineTask with { Prompt = "changed" }], baselineParams, baselineCapabilities)
            .DefinitionHash.Should().NotBe(baseline);
        compiler.CompileDefinition([baselineTask with { ExecutionAccess = AgentTaskExecutionAccess.Write }], baselineParams, baselineCapabilities)
            .DefinitionHash.Should().NotBe(baseline);
        compiler.CompileDefinition([baselineTask], new CacheSafeParams { SystemPrompt = "system-b", ModelId = "model-a", ThinkingBudget = 1 }, baselineCapabilities)
            .DefinitionHash.Should().NotBe(baseline);
        compiler.CompileDefinition([baselineTask], new CacheSafeParams { SystemPrompt = "system-a", ModelId = "model-b", ThinkingBudget = 1 }, baselineCapabilities)
            .DefinitionHash.Should().NotBe(baseline);
        compiler.CompileDefinition([baselineTask], baselineParams, ToolCapabilitySet.CreateUnrestricted(["Read", "Write"]))
            .DefinitionHash.Should().NotBe(baseline);
    }

    [Fact]
    public void Validate_RejectsDuplicateIdsUnknownDependenciesCyclesAndNormalizedIdCollisions()
    {
        var duplicate = () => AgentTaskWorkflowCompiler.Validate([Task("a"), Task("a")]);
        var unknown = () => AgentTaskWorkflowCompiler.Validate([Task("a", ["missing"])]);
        var cycle = () => AgentTaskWorkflowCompiler.Validate([Task("a", ["b"]), Task("b", ["a"])]);
        var normalizedCollision = () => AgentTaskWorkflowCompiler.Validate([Task("a b"), Task("a-b")]);

        duplicate.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate*");
        unknown.Should().Throw<InvalidOperationException>().WithMessage("*unknown*");
        cycle.Should().Throw<InvalidOperationException>().WithMessage("*cycle*");
        normalizedCollision.Should().Throw<InvalidOperationException>().WithMessage("*normalized executor IDs*");
    }

    private static IReadOnlyList<AgentWorkflowTask> CreateRandomDag(Random random, int taskCount)
    {
        var tasks = new List<AgentWorkflowTask>(taskCount);
        for (var index = 0; index < taskCount; index++)
        {
            var id = $"task-{index:D2}";
            var dependencies = Enumerable.Range(0, index)
                .Where(_ => random.NextDouble() < 0.2)
                .Select(dependency => $"task-{dependency:D2}")
                .ToArray();
            tasks.Add(Task(
                id,
                dependencies,
                random.NextDouble() < 0.35 ? AgentTaskExecutionAccess.Write : AgentTaskExecutionAccess.ReadOnly));
        }
        return tasks.OrderBy(_ => random.Next()).ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<string>> ComputeLevels(AgentTaskWorkflowDefinition definition)
    {
        var remaining = definition.EffectiveDependencies.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Count,
            StringComparer.OrdinalIgnoreCase);
        var levels = new List<IReadOnlyList<string>>();
        while (remaining.Count > 0)
        {
            var level = remaining.Where(pair => pair.Value == 0)
                .Select(pair => pair.Key)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            level.Should().NotBeEmpty("the effective graph must remain acyclic");
            levels.Add(level);
            foreach (var taskId in level)
                remaining.Remove(taskId);
            foreach (var taskId in remaining.Keys.ToArray())
                remaining[taskId] = definition.EffectiveDependencies[taskId].Count(remaining.ContainsKey);
        }
        return levels;
    }

    private static bool IsReachable(
        string ancestor,
        string descendant,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dependencies)
    {
        var pending = new Stack<string>();
        pending.Push(descendant);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
                continue;
            foreach (var dependency in dependencies[current])
            {
                if (string.Equals(dependency, ancestor, StringComparison.OrdinalIgnoreCase))
                    return true;
                pending.Push(dependency);
            }
        }
        return false;
    }

    private static AgentTaskWorkflowCompiler CreateCompiler()
        => new(Substitute.For<IAgentRunner>(), NullLogger<AgentTaskWorkflowCompiler>.Instance);

    private static IReadOnlyList<string> SerializeEdges(AgentTaskWorkflowDefinition definition)
        => definition.EffectiveDependencies
            .SelectMany(pair => pair.Value.Select(dependency => $"{dependency}->{pair.Key}"))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static async Task<AgentWorkflowResult> RunAsync(
        IAgentRunner runner,
        IReadOnlyList<AgentWorkflowTask> tasks)
    {
        var compiler = new AgentTaskWorkflowCompiler(runner, NullLogger<AgentTaskWorkflowCompiler>.Instance);
        var host = new AgentTaskWorkflowHost(NullLogger<AgentTaskWorkflowHost>.Instance);
        var workflow = compiler.Compile(tasks, null, null);
        return await host.RunAsync(workflow, tasks, TestContext.Current.CancellationToken);
    }

    private static AgentWorkflowTask Task(
        string id,
        IReadOnlyList<string>? dependencies = null,
        AgentTaskExecutionAccess access = AgentTaskExecutionAccess.ReadOnly)
        => new(id, $"prompt-{id}", Description: id, DependsOn: dependencies, ExecutionAccess: access);
}
