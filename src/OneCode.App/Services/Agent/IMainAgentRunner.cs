using Microsoft.Agents.AI;
using System.Threading.Channels;

namespace OneCode.App.Services.Agent;

/// <summary>
/// Main-query MAF agent runner (streaming loop + shared-transaction agent build).
/// </summary>
public interface IMainAgentRunner
{
    Task<AIAgent> BuildAsAIAgentAsync(
        MainAgentRunOptions options,
        CancellationToken ct = default);

    Task<MainAgentRunResult> RunStreamingAsync(
        MainAgentRunOptions options,
        ChannelWriter<object> writer,
        CancellationToken ct = default);
}
