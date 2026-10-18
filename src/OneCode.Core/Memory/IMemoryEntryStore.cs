namespace OneCode.Core.Memory;

/// <summary>
/// 结构化记忆条目的存储抽象。
/// </summary>
/// <remarks>
/// <para>
/// 把调用方（<c>MemoryService</c>、<c>AutoDreamService</c>、<c>MemoryCommand</c>）与物理存储解耦：
/// 生产实现按作用域读写 <c>MEMORY.md</c>，测试使用内存实现（<c>InMemoryMemoryEntryStore</c>）。
/// </para>
///
/// <para>
/// <b>作用域语义</b>：调用方指定 <see cref="MemoryScope.User"/>（全局、跨项目）或
/// <see cref="MemoryScope.Project"/>（当前工作目录）。物理位置由实现内部解析，调用方不接触文件路径。
/// </para>
///
/// <para>
/// <b>线程安全</b>：同一作用域的并发写入必须由实现串行化（例如按作用域加锁）；读取无需加锁。
/// </para>
///
/// <para>
/// <b>过期语义</b>：<see cref="LoadAsync"/> 过滤过期条目（<see cref="MemoryEntry.IsExpired"/>）；
/// <see cref="LoadAllAsync"/> 保留过期条目供管理命令使用；<see cref="PruneAsync"/> 物理删除过期条目
/// 并执行容量限制。
/// </para>
/// </remarks>
public interface IMemoryEntryStore
{
    /// <summary>
    /// 读取指定作用域内所有未过期条目。用户级为全局跨项目，项目级为当前工作目录。
    /// </summary>
    Task<IReadOnlyList<MemoryEntry>> LoadAsync(MemoryScope scope, CancellationToken ct = default);

    /// <summary>
    /// 读取指定作用域内的全部条目（含已过期），供需要展示完整清单的管理命令使用（如 <c>/memory list</c>）。
    /// </summary>
    Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(MemoryScope scope, CancellationToken ct = default);

    /// <summary>
    /// 原子写入条目：同键条目被覆盖（保留原始 <see cref="MemoryEntry.CreatedAt"/>），新条目追加。
    /// 不在此处清理过期条目——那是 <see cref="PruneAsync"/> 的职责。
    /// </summary>
    Task UpsertAsync(MemoryScope scope, IEnumerable<MemoryEntry> entries, CancellationToken ct = default);

    /// <summary>
    /// 删除指定键的条目。
    /// </summary>
    /// <returns>删除成功返回 <see langword="true"/>；键不存在返回 <see langword="false"/>。</returns>
    Task<bool> RemoveAsync(MemoryScope scope, string key, CancellationToken ct = default);

    /// <summary>
    /// 清空指定作用域的全部条目（删除该作用域的底层存储）。
    /// </summary>
    Task ClearAsync(MemoryScope scope, CancellationToken ct = default);

    /// <summary>
    /// 删除全部过期条目，并在超过容量上限时优先淘汰最旧的非人工条目。
    /// 作用于指定作用域：用户级为全局跨项目，项目级为当前工作目录。
    /// </summary>
    /// <returns>被删除的条目数（过期 + 淘汰）。</returns>
    /// <remarks>
    /// 超过容量上限时的淘汰顺序（价值最低者先出）：
    /// <list type="number">
    /// <item><see cref="MemoryEntry.Source"/> 为 <c>manual</c> 的条目<b>豁免</b>——它们表达用户明确意图，
    ///   不参与自动淘汰。</item>
    /// <item>其余条目按 <see cref="MemoryEntry.UpdatedAt"/> 升序，最旧的先出。</item>
    /// </list>
    /// </remarks>
    Task<int> PruneAsync(MemoryScope scope, CancellationToken ct = default);
}
