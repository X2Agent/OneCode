using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using OneCode.Core.Collections;
using OneCode.Core.Domain;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// 针对 MAF <see cref="AgentSessionStateBag"/> 的扩展方法，封装 StateBag 的类型安全访问。
/// </summary>
/// <remarks>
/// 放在 Infrastructure 层（而非 Core）因为 <see cref="AgentSessionStateBag"/> 属于 MAF。
/// <para>
/// <b>MAF 泛型约束</b>：<c>GetValue&lt;T&gt;</c>/<c>SetValue&lt;T&gt;</c> 有 <c>where T : class</c> 约束，
/// 值类型通过 <c>object</c> 装箱存取，引用类型直接用泛型 API。
/// </para>
/// <para>
/// <b>线程安全</b>：所有 <c>Increment*</c>（RMW）和 <c>GetOrInitialize*</c>（check-then-act）
/// 方法通过 per-session 锁原子化。跨 <c>await</c> 的 RMW 序列需更高级串行化机制，不在本层解决。
/// </para>
/// </remarks>
public static class SessionStateExtensions
{
    /// <summary>
    /// Per-session 锁映射。<see cref="ConditionalWeakTable{TKey,TValue}"/> 不会阻止
    /// StateBag 被 GC 回收，避免内存泄漏。<see cref="ConditionalWeakTable{TKey,TValue}.GetOrCreateValue"/> 本身线程安全。
    /// </summary>
    private static readonly ConditionalWeakTable<AgentSessionStateBag, object> _sessionLocks = new();

    private static object GetLock(AgentSessionStateBag stateBag)
        => _sessionLocks.GetOrCreateValue(stateBag);

    // 简单值类型（通过 object 装箱）

    /// <summary>未设置时返回 <see cref="AgentState.Active"/>。</summary>
    public static AgentState GetCurrentState(this AgentSessionStateBag stateBag)
    {
        var val = stateBag.GetValue<object>(SessionStateKeys.CurrentState);
        return val is AgentState s ? s : AgentState.Active;
    }

    public static void SetCurrentState(this AgentSessionStateBag stateBag, AgentState state)
        => stateBag.SetValue(SessionStateKeys.CurrentState, (object)state);

    /// <summary>未设置时返回 0。</summary>
    public static int GetConsecutiveFailures(this AgentSessionStateBag stateBag)
    {
        var val = stateBag.GetValue<object>(SessionStateKeys.ConsecutiveFailures);
        return val is int i ? i : 0;
    }

    public static void SetConsecutiveFailures(this AgentSessionStateBag stateBag, int value)
        => stateBag.SetValue(SessionStateKeys.ConsecutiveFailures, (object)value);

    /// <summary>原子 RMW。</summary>
    public static int IncrementConsecutiveFailures(this AgentSessionStateBag stateBag)
    {
        lock (GetLock(stateBag))
        {
            var current = stateBag.GetConsecutiveFailures() + 1;
            stateBag.SetConsecutiveFailures(current);
            return current;
        }
    }

    public static void ResetConsecutiveFailures(this AgentSessionStateBag stateBag)
        => stateBag.SetConsecutiveFailures(0);

    /// <summary>未设置时返回 0。</summary>
    public static int GetTotalToolCalls(this AgentSessionStateBag stateBag)
    {
        var val = stateBag.GetValue<object>(SessionStateKeys.TotalToolCalls);
        return val is int i ? i : 0;
    }

    /// <summary>原子 RMW。</summary>
    public static int IncrementTotalToolCalls(this AgentSessionStateBag stateBag)
    {
        lock (GetLock(stateBag))
        {
            var current = stateBag.GetTotalToolCalls() + 1;
            stateBag.SetValue(SessionStateKeys.TotalToolCalls, (object)current);
            return current;
        }
    }

    /// <summary>未设置时返回 0。</summary>
    public static int GetEditsSinceLastBuild(this AgentSessionStateBag stateBag)
    {
        var val = stateBag.GetValue<object>(SessionStateKeys.EditsSinceLastBuild);
        return val is int i ? i : 0;
    }

    public static void SetEditsSinceLastBuild(this AgentSessionStateBag stateBag, int value)
        => stateBag.SetValue(SessionStateKeys.EditsSinceLastBuild, (object)value);

    /// <summary>原子 RMW。</summary>
    public static int IncrementEditsSinceLastBuild(this AgentSessionStateBag stateBag)
    {
        lock (GetLock(stateBag))
        {
            var current = stateBag.GetEditsSinceLastBuild() + 1;
            stateBag.SetEditsSinceLastBuild(current);
            return current;
        }
    }

    public static void ResetEditsSinceLastBuild(this AgentSessionStateBag stateBag)
        => stateBag.SetEditsSinceLastBuild(0);

    // 集合类型（直接用泛型 API，T : class 满足）

    /// <summary>
    /// 获取或初始化最近工具调用记录缓冲区。
    /// 未设置时创建容量为 <paramref name="capacity"/> 的 <see cref="FixedSizeRingBuffer{T}"/>。
    /// 原子 check-then-act。
    /// </summary>
    public static FixedSizeRingBuffer<ToolCallRecord> GetOrInitializeRecentToolCalls(
        this AgentSessionStateBag stateBag, int capacity = 50)
    {
        lock (GetLock(stateBag))
        {
            var buf = stateBag.GetValue<FixedSizeRingBuffer<ToolCallRecord>>(SessionStateKeys.RecentToolCalls);
            if (buf is not null)
                return buf;

            buf = new FixedSizeRingBuffer<ToolCallRecord>(capacity);
            stateBag.SetValue(SessionStateKeys.RecentToolCalls, buf);
            return buf;
        }
    }

    /// <summary>
    /// 获取或初始化已修改文件路径集合。
    /// 未设置时创建 <see cref="HashSet{T}"/>（OrdinalIgnoreCase）。原子 check-then-act。
    /// </summary>
    public static HashSet<string> GetOrInitializeModifiedFiles(this AgentSessionStateBag stateBag)
    {
        lock (GetLock(stateBag))
        {
            var set = stateBag.GetValue<HashSet<string>>(SessionStateKeys.ModifiedFiles);
            if (set is not null)
                return set;

            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            stateBag.SetValue(SessionStateKeys.ModifiedFiles, set);
            return set;
        }
    }

    /// <summary>
    /// 获取或初始化结构化工具执行上下文。
    /// 未设置时创建默认实例（IsError=false, Guidance=None）。原子 check-then-act。
    /// </summary>
    /// <remarks>
    /// 返回引用类型，调用方拿到引用后可直接修改属性（如 <c>ctx.IsError = true</c>），
    /// 无需额外 Set 调用。重置请用 <see cref="ResetToolExecutionContext"/>。
    /// </remarks>
    public static ToolExecutionContext GetOrInitializeToolExecutionContext(this AgentSessionStateBag stateBag)
    {
        lock (GetLock(stateBag))
        {
            var ctx = stateBag.GetValue<ToolExecutionContext>(SessionStateKeys.ToolExecutionContext);
            if (ctx is not null)
                return ctx;

            ctx = new ToolExecutionContext();
            stateBag.SetValue(SessionStateKeys.ToolExecutionContext, ctx);
            return ctx;
        }
    }

    /// <summary>
    /// 重置工具执行上下文为初始状态（IsError=false, Guidance=None）。
    /// 由 <c>ToolResultUnwrapMiddleware</c> 在每次工具调用前调用，
    /// 确保上一次调用的残留状态不污染本次判定。
    /// </summary>
    public static void ResetToolExecutionContext(this AgentSessionStateBag stateBag)
    {
        lock (GetLock(stateBag))
        {
            var ctx = stateBag.GetOrInitializeToolExecutionContext();
            ctx.IsError = false;
            ctx.Guidance = GuidanceKind.None;
            ctx.IsVerificationFailure = false;
        }
    }
}
