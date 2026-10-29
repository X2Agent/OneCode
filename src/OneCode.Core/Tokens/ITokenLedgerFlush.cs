namespace OneCode.Core.Tokens;

/// <summary>
/// 可显式收尾的 token 账本：run 或宿主结束时把内存中的待写增量合并落盘。
/// 记账主路径只依赖 <see cref="ITokenLedger"/>，只有生命周期边界需要此能力。
/// </summary>
public interface ITokenLedgerFlush
{
    /// <summary>把当前待写增量持久化；并发调用由实现串行化，不得丢增量。</summary>
    Task FlushAsync(CancellationToken ct = default);
}
