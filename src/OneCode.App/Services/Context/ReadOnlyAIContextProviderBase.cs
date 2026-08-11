// MAAI001 suppressed: AIContextProvider uses experimental MAF APIs
using Microsoft.Agents.AI;

namespace OneCode.App.Services.Context;

/// <summary>
/// 只读 Context Provider 基类——提供 <see cref="StoreAIContextAsync"/> 的空实现。
///
/// <para>
/// MAF 的 <see cref="AIContextProvider"/> 要求子类实现 <c>StoreAIContextAsync</c>，
/// 但多数 Provider 只读取上下文（注入 system message / tools），不回写。
/// 此基类提供 <c>StoreAIContextAsync</c> 的默认空实现（返回 <see cref="ValueTask.CompletedTask"/>），
/// 让只读 Provider 只需 override <c>ProvideAIContextAsync</c>。
/// </para>
///
/// <para>
/// 双向 Provider（如 <c>SessionMemoryContextProvider</c>）应直接继承 <see cref="AIContextProvider"/>，
/// 不要使用此基类。
/// </para>
/// </summary>
public abstract class ReadOnlyAIContextProviderBase : AIContextProvider
{
    protected override ValueTask StoreAIContextAsync(
        AIContextProvider.InvokedContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
