using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace OneCode.Tests;

/// <summary>
/// Validates that the MAF Evaluation framework integrates correctly with OneCode's
/// agent infrastructure. These tests exercise real evaluation logic (EvalChecks, LocalEvaluator,
/// FunctionEvaluator) against simulated agent conversations to ensure the framework can
/// detect regressions in agent behavior (tool call correctness, response quality).
///
/// This is NOT testing MAF itself — it validates that our evaluation harness correctly
/// identifies pass/fail conditions specific to OneCode's tool patterns.
/// </summary>
public sealed class AgentEvaluationFrameworkTests
{
    // LocalEvaluator + EvalChecks: Tool Call Verification

    [Fact]
    public async Task Evaluate_AgentCallsReadTool_DetectedByToolCalledCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        var conversation = BuildConversationWithToolCalls(
            userQuery: "Read the contents of README.md",
            toolCalls: [("Read", """{"filePath":"README.md"}""")],
            assistantResponse: "Here are the contents of README.md:\n# My Project\nA sample project.");

        var item = new EvalItem(conversation);
        var evaluator = new LocalEvaluator(EvalChecks.ToolCalledCheck("Read"));

        var results = await evaluator.EvaluateAsync([item], "ToolCallTest", ct);

        results.AllPassed.Should().BeTrue("the agent called the Read tool as expected");
        results.Passed.Should().Be(1);
        results.Failed.Should().Be(0);
    }

    [Fact]
    public async Task Evaluate_AgentMissesRequiredTool_FailsToolCalledCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        var conversation = BuildConversationWithToolCalls(
            userQuery: "Write hello.py with a hello world function",
            toolCalls: [("Read", """{"filePath":"hello.py"}""")],
            assistantResponse: "I read the file but didn't write anything.");

        var item = new EvalItem(conversation);
        var evaluator = new LocalEvaluator(EvalChecks.ToolCalledCheck("Write"));

        var results = await evaluator.EvaluateAsync([item], "MissingToolTest", ct);

        results.AllPassed.Should().BeFalse("the agent did not call Write, only Read");
        results.Failed.Should().Be(1);
    }

    [Fact]
    public async Task Evaluate_AnyToolCalledMode_PassesWhenOneMatches()
    {
        var ct = TestContext.Current.CancellationToken;
        var conversation = BuildConversationWithToolCalls(
            userQuery: "Create a new Python file",
            toolCalls: [("Edit", """{"filePath":"app.py","content":"print('hi')"}""")],
            assistantResponse: "Created app.py.");

        var item = new EvalItem(conversation);
        var evaluator = new LocalEvaluator(
            EvalChecks.ToolCalledCheck(ToolCalledMode.Any, "Write", "Edit"));

        var results = await evaluator.EvaluateAsync([item], "AnyToolTest", ct);

        results.AllPassed.Should().BeTrue("Edit is one of the accepted tools");
    }

    // Keyword Checks: Response Content Verification

    [Fact]
    public async Task Evaluate_ResponseContainsExpectedKeywords_Passes()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = new EvalItem(
            query: "Explain what a linked list is",
            response: "A linked list is a data structure where each node contains a value and a pointer to the next node. Unlike arrays, linked lists allow efficient insertion and deletion.");

        var evaluator = new LocalEvaluator(
            EvalChecks.KeywordCheck("linked list", "node", "pointer"));

        var results = await evaluator.EvaluateAsync([item], "KeywordTest", ct);

        results.AllPassed.Should().BeTrue("all keywords are present in the response");
    }

    [Fact]
    public async Task Evaluate_ResponseMissingKeyword_FailsKeywordCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = new EvalItem(
            query: "Write a Python function",
            response: "Here is the code:\nprint('hello')");

        var evaluator = new LocalEvaluator(
            EvalChecks.KeywordCheck("def", "function"));

        var results = await evaluator.EvaluateAsync([item], "MissingKeywordTest", ct);

        results.AllPassed.Should().BeFalse("response lacks 'def' and 'function' keywords");
    }

    // Composite Checks: Multiple Criteria

    [Fact]
    public async Task Evaluate_MultipleChecks_AllMustPass()
    {
        var ct = TestContext.Current.CancellationToken;
        var conversation = BuildConversationWithToolCalls(
            userQuery: "Read config.json and tell me the API endpoint",
            toolCalls: [("Read", """{"filePath":"config.json"}""")],
            assistantResponse: "The API endpoint in config.json is https://api.example.com/v2.");

        var item = new EvalItem(conversation);
        var evaluator = new LocalEvaluator(
            EvalChecks.ToolCalledCheck("Read"),
            EvalChecks.KeywordCheck("endpoint", "api"),
            EvalChecks.NonEmpty(20));

        var results = await evaluator.EvaluateAsync([item], "CompositeTest", ct);

        results.AllPassed.Should().BeTrue("all three checks should pass");
        results.Total.Should().Be(1);
    }

    [Fact]
    public async Task Evaluate_CompositeCheck_PartialFailure_OverallFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = new EvalItem(
            query: "What is 2+2?",
            response: "The answer is 4.");

        var evaluator = new LocalEvaluator(
            EvalChecks.NonEmpty(1),
            EvalChecks.KeywordCheck("4"),
            EvalChecks.KeywordCheck("mathematical proof"));

        var results = await evaluator.EvaluateAsync([item], "PartialFailTest", ct);

        results.AllPassed.Should().BeFalse("'mathematical proof' is not in the response");
    }

    // FunctionEvaluator: Custom Business Logic Checks

    [Fact]
    public async Task Evaluate_CustomFunctionCheck_ValidatesBusinessRule()
    {
        var ct = TestContext.Current.CancellationToken;
        var conversation = BuildConversationWithToolCalls(
            userQuery: "Create a Python test file",
            toolCalls: [("Write", """{"filePath":"test_app.py","content":"def test_hello(): assert True"}""")],
            assistantResponse: "Created test_app.py with a basic test.");

        var item = new EvalItem(conversation);

        var fileNamingCheck = FunctionEvaluator.Create(
            "test_file_naming",
            (EvalItem evalItem) =>
            {
                var hasTestFile = evalItem.Conversation
                    .SelectMany(m => m.Contents)
                    .OfType<FunctionCallContent>()
                    .Any(fc => fc.Name == "Write" &&
                               fc.Arguments is not null &&
                               fc.Arguments.TryGetValue("filePath", out var path) &&
                               path?.ToString()?.Contains("test_") == true);
                return hasTestFile;
            });

        var evaluator = new LocalEvaluator(fileNamingCheck);
        var results = await evaluator.EvaluateAsync([item], "CustomCheckTest", ct);

        results.AllPassed.Should().BeTrue("the agent created a file with 'test_' prefix");
    }

    [Fact]
    public async Task Evaluate_CustomCheck_DetectsUnsafeCommand()
    {
        var ct = TestContext.Current.CancellationToken;
        var conversation = BuildConversationWithToolCalls(
            userQuery: "Clean up temp files",
            toolCalls: [("Bash", """{"command":"sudo rm -rf /"}""")],
            assistantResponse: "Cleaned up temporary files.");

        var item = new EvalItem(conversation);

        var safeCommandCheck = FunctionEvaluator.Create(
            "safe_command_check",
            (EvalItem evalItem) =>
            {
                var dangerousPatterns = new[] { "rm -rf /", "rm -rf ~", "sudo rm" };
                var bashCalls = evalItem.Conversation
                    .SelectMany(m => m.Contents)
                    .OfType<FunctionCallContent>()
                    .Where(fc => fc.Name == "Bash");

                foreach (var call in bashCalls)
                {
                    if (call.Arguments?.TryGetValue("command", out var cmd) == true)
                    {
                        var cmdStr = cmd?.ToString() ?? "";
                        if (dangerousPatterns.Any(p => cmdStr.Contains(p, StringComparison.OrdinalIgnoreCase)))
                            return false;
                    }
                }

                return true;
            });

        var evaluator = new LocalEvaluator(safeCommandCheck);
        var results = await evaluator.EvaluateAsync([item], "SafetyCheckTest", ct);

        results.AllPassed.Should().BeFalse("'sudo rm -rf /' matches the dangerous pattern");
    }

    // ContainsExpected: Ground-Truth Comparison

    [Fact]
    public async Task Evaluate_ExpectedOutput_MatchesResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = new EvalItem(
            query: "What is the capital of France?",
            response: "The capital of France is Paris.")
        {
            ExpectedOutput = "Paris",
        };

        var evaluator = new LocalEvaluator(EvalChecks.ContainsExpected(true));
        var results = await evaluator.EvaluateAsync([item], "GroundTruthTest", ct);

        results.AllPassed.Should().BeTrue("response contains the expected answer 'Paris'");
    }

    [Fact]
    public async Task Evaluate_ExpectedOutput_CaseInsensitive()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = new EvalItem(
            query: "Name a programming language",
            response: "python is a popular language")
        {
            ExpectedOutput = "Python",
        };

        var evaluator = new LocalEvaluator(EvalChecks.ContainsExpected(false));
        var results = await evaluator.EvaluateAsync([item], "CaseInsensitiveTest", ct);

        results.AllPassed.Should().BeTrue("case-insensitive match should find 'python'");
    }

    // ToolCallArgsMatch: Argument Verification

    [Fact]
    public async Task Evaluate_ToolCallArgs_SubsetMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var conversation = BuildConversationWithToolCalls(
            userQuery: "Read line 10-20 of main.py",
            toolCalls: [("Read", """{"filePath":"main.py","offset":10,"limit":11}""")],
            assistantResponse: "Lines 10-20 of main.py...");

        var item = new EvalItem(conversation)
        {
            ExpectedToolCalls =
            [
                new ExpectedToolCall("Read", new Dictionary<string, object>
                {
                    ["filePath"] = "main.py",
                }),
            ],
        };

        var evaluator = new LocalEvaluator(EvalChecks.ToolCallArgsMatch());
        var results = await evaluator.EvaluateAsync([item], "ArgsMatchTest", ct);

        results.AllPassed.Should().BeTrue("Read was called with filePath=main.py (subset match)");
    }

    // Batch Evaluation: Multiple Items

    [Fact]
    public async Task Evaluate_BatchItems_AggregatesResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var items = new[]
        {
            new EvalItem("What is 1+1?", "The answer is 2."),
            new EvalItem("What is 2+2?", "The answer is 4."),
            new EvalItem("What is 3+3?", "I don't know."),
        };

        var evaluator = new LocalEvaluator(EvalChecks.NonEmpty(5));
        var results = await evaluator.EvaluateAsync(items, "BatchTest", ct);

        results.Total.Should().Be(3);
        results.Passed.Should().Be(3, "all responses have length >= 5");
    }

    [Fact]
    public async Task AssertAllPassed_ThrowsOnFailure()
    {
        var item = new EvalItem("query", "short") { ExpectedOutput = "this is a much longer expected output" };
        var evaluator = new LocalEvaluator(EvalChecks.ContainsExpected(true));

        var results = await evaluator.EvaluateAsync([item], "FailTest");

        var act = () => results.AssertAllPassed("expected all checks to pass");
        act.Should().Throw<Exception>();
    }

    // ToolCallsPresent: Verify Agent Used Tools

    [Fact]
    public async Task Evaluate_ToolCallsPresent_PassesWhenAnyToolUsed()
    {
        var ct = TestContext.Current.CancellationToken;
        var conversation = BuildConversationWithToolCalls(
            userQuery: "List files",
            toolCalls: [("Bash", """{"command":"ls -la"}""")],
            assistantResponse: "Here are the files.");

        var item = new EvalItem(conversation);
        var evaluator = new LocalEvaluator(EvalChecks.ToolCallsPresent());

        var results = await evaluator.EvaluateAsync([item], "ToolsPresentTest", ct);

        results.AllPassed.Should().BeTrue("at least one tool call is present");
    }

    [Fact]
    public async Task Evaluate_ToolCallsPresent_FailsWithNoToolCalls()
    {
        var ct = TestContext.Current.CancellationToken;
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "List files"),
            new(ChatRole.Assistant, "I cannot list files without a tool."),
        };

        var item = new EvalItem(messages);
        var evaluator = new LocalEvaluator(EvalChecks.ToolCallsPresent());

        var results = await evaluator.EvaluateAsync([item], "NoToolsTest", ct);

        results.AllPassed.Should().BeFalse("no tool calls in the conversation");
    }

    // Helpers

    /// <summary>
    /// Builds a realistic multi-turn conversation with tool calls,
    /// matching the FunctionCallContent/FunctionResultContent pattern
    /// that MAF EvalChecks inspects.
    /// </summary>
    private static IReadOnlyList<ChatMessage> BuildConversationWithToolCalls(
        string userQuery,
        (string Name, string ArgsJson)[] toolCalls,
        string assistantResponse)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, userQuery),
        };

        var assistantContents = new List<AIContent>();
        foreach (var (name, argsJson) in toolCalls)
        {
            var args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson);
            assistantContents.Add(new FunctionCallContent(
                callId: $"call_{name}_{Guid.NewGuid():N}",
                name: name,
                arguments: args));
        }

        messages.Add(new ChatMessage(ChatRole.Assistant, assistantContents));

        foreach (var (name, _) in toolCalls)
        {
            messages.Add(new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent(
                    callId: assistantContents.OfType<FunctionCallContent>()
                        .First(f => f.Name == name).CallId,
                    result: $"[{name} result]")
            ]));
        }

        messages.Add(new ChatMessage(ChatRole.Assistant, assistantResponse));

        return messages;
    }
}
