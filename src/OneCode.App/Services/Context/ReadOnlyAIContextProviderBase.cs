// MAAI001 suppressed: AIContextProvider uses experimental MAF APIs
using Microsoft.Agents.AI;

namespace OneCode.App.Services.Context;

/// <summary>
/// 只读 Context Provider 基类：提供 <c>StoreAIContextAsync</c> 的空实现，子类只需 override
/// <c>ProvideAIContextAsync</c>。需要回写上下文的 Provider 直接继承 <see cref="AIContextProvider"/>。
/// </summary>
public abstract class ReadOnlyAIContextProviderBase : AIContextProvider
{
    protected override ValueTask StoreAIContextAsync(
        AIContextProvider.InvokedContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
