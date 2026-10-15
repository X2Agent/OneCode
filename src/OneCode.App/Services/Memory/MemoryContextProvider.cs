// MAAI001 suppressed: AIContextProvider uses experimental MAF APIs
using Microsoft.Agents.AI;
using OneCode.App.Services.Context;
using OneCode.Core.Memory;

namespace OneCode.App.Services.Memory;

/// <summary>
/// 持久记忆上下文注入 Provider——每次 Agent 调用时从 <see cref="IMemoryService"/> 读取当前有效的
/// 用户级与项目级 <c>MEMORY.md</c> 条目完整正文，通过 <see cref="AIContext.Instructions"/> 注入模型上下文。
/// </summary>
/// <remarks>
/// <para>
/// 选择 <see cref="AIContext.Instructions"/> 而非 <see cref="AIContext.Messages"/>：
/// MAF 会将各 Provider 返回的 <c>Instructions</c> 合并进 <c>ChatOptions.Instructions</c>，
/// 与主系统提示词同属一条系统指令，不会额外增加 <c>ChatMessage(ChatRole.System)</c> 块，
/// 从而避免多 Provider 叠加时突破 Anthropic API 最多 4 个 <c>cache_control</c> 断点的硬上限。
/// </para>
/// <para>
/// 读取异常降级为空上下文并记录警告，不阻断主对话或整合流程。
/// </para>
/// </remarks>
public sealed class MemoryContextProvider(
    IMemoryService memoryService,
    ILogger<MemoryContextProvider>? logger = null) : ReadOnlyAIContextProviderBase
{
    private readonly IMemoryService _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
    private readonly ILogger<MemoryContextProvider>? _logger = logger;

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        AIContextProvider.InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var memoryPrompt = await _memoryService
                .LoadMemoryPromptAsync(cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(memoryPrompt))
                return new AIContext();

            return new AIContext
            {
                Instructions = memoryPrompt,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MemoryContextProvider failed to load memory prompt; continuing without memory context");
            return new AIContext();
        }
    }
}
