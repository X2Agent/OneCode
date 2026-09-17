# OneCode.Tests 单元测试约束

> **本文件是 AI 编码代理的强制约束。** 在对 `OneCode.Tests` 目录进行任何新增或修改之前，必须通读并遵守本文档中的所有规则。
> 通用编码规范见上级 [AGENTS.md](../AGENTS.md)。当本文档与上级文档冲突时，以本文档为准。

---

## 核心原则

> **测试即防回归，不是覆盖率数字填充物。**

每一个测试用例必须能够回答一个问题：**"如果这段代码回归了，这个测试能发现吗？"**

如果答案是"不能"，则该测试为无意义测试，不得写入。

---

## 必须删除/禁写的测试模式

以下模式的测试**严禁新增**，已存在的也应积极清理：

### 1. 静态属性/常量验证

❌ **禁止：**
```csharp
[Fact]
public void ToolName_IsAgent()
{
    var sut = new AgentTool();
    sut.Name.Should().Be("Agent");
}
```
验证一个固定的字符串属性值。代码回归时属性名变了编译器就会报错，这个测试不会比编译检查多做任何事。

### 2. JSON Schema / 元数据结构验证

❌ **禁止：**
```csharp
[Fact]
public void InputSchema_HasRequiredProperties()
{
    var sut = new AgentTool();
    var props = sut.InputSchema.GetProperty("properties");
    props.TryGetProperty("prompt", out _).Should().BeTrue();
}
```
Schema 结构是静态数据，不是可回归的业务逻辑。验证它和验证常量没有区别。

### 3. 初始状态检查

❌ **禁止：**
```csharp
[Fact]
public void LastCacheSafeParams_StartsNull() { ... }
public void RegisteredTeams_Empty_ByDefault() { ... }
public void EmptyRegistry_GetAllTools_ReturnsEmpty() { ... }
```
对象的初始状态是构造函数的职责，不是业务逻辑。如果初始状态错了，依赖它的真实测试自然会失败。

### 4. 元数据存取验证（写后立即读）

❌ **禁止：**
```csharp
[Fact]
public void ForkedSession_MessagesContainForkRef()
{
    conv.Metadata["forkedFromSessionId"] = parentId;
    conv.Metadata["forkedFromSessionId"].Should().Be(parentId);
}
```
这是测试 Dictionary 的功能，不是测试业务代码。从来不会有回归要防。

### 5. 表达式断言（未调用任何被测方法）

❌ **禁止：**
```csharp
[Fact]
public void MemoryExtract_TooFewMessages_Skips()
{
    var conv = CreateConversation(2);
    var shouldExtract = conv.Messages.Count >= 4;  // 自己写的表达式
    shouldExtract.Should().BeFalse();               // 断言自己写的表达式
}
```
代码里根本没有 `>= 4` 这个表达式，这是把测试代码当被测代码。

### 6. 反射计数 / 类型枚举

❌ **禁止：**
```csharp
[Fact]
public void AllConcreteTuiEventTypes_AreAccountedFor()
{
    var allTypes = typeof(TuiEvent).Assembly.GetTypes()
        .Where(t => t.IsAssignableTo(typeof(TuiEvent)) && !t.IsAbstract);
    allTypes.Should().HaveCount(10);
}
```
脆弱的数字硬编码，新增一个事件类型就失败，但不代表任何业务错误。

### 7. 纯 Mock 行为验证（无业务断言）

❌ **禁止：**
```csharp
[Fact]
public async Task ExecuteAsync_StopsApplication()
{
    var lifetime = Substitute.For<IHostApplicationLifetime>();
    await cmd.ExecuteAsync([], ct);
    lifetime.Received(1).StopApplication();  // ← 仅此一个断言
}
```
验证了 Mock 对象的方法被调用，但没有验证任何真实的业务产出物。如果实现改为调用 `lifetime.StopApplication()` 两次，测试仍然通过，但业务已回归。

### 8. 被前置闸门遮蔽的守卫测试（假绿）

❌ **禁止：**
```csharp
[Fact]
public async Task AutoDream_RefusesToDeleteManualEntry()
{
    // 写入一条 manual: 前缀的条目
    await SeedAsync(key: "manual:user-pinned", source: "manual");

    var written = await sut.ApplyChangesAsync(["""{"action":"delete","key":"manual:user-pinned"}"""]);

    written.Should().Be(0, "manual entries must not be deletable");
}
```

被测代码有**两道串联的守卫**：先判 key 前缀 `manual:`，再判 `Source == "manual"`。
本用例的 key 恰好带 `manual:` 前缀，**被第一道闸先拦下**，第二道闸（`Source` 判定）
无论是否被破坏，测试都会通过。

也就是说：把这个测试当作 `Source` 守卫的证明是**假的**。删掉第二道闸，它依然全绿。

✅ **正确：每条守卫都需要一个能绕过前面所有闸门的用例**
```csharp
[Fact]
public async Task AutoDream_RefusesToDeleteManualEntry()
{
    // key 刻意不带 manual: 前缀（绕过前缀闸），仅靠 source=manual 触发 Source 守卫。
    // 用户可直接编辑 MEMORY.md 写入任意 key，这条路径是真实可达的。
    await SeedAsync(key: "fact:user-edited", source: "manual");

    var written = await sut.ApplyChangesAsync(["""{"action":"delete","key":"fact:user-edited"}"""]);

    written.Should().Be(0, "source=manual entries must not be deletable regardless of key prefix");
}
```

**判定方法**：写出用例后问自己——「破坏我真正想验证的那道闸，这个测试会失败吗？」
如果不确定，就去做一次反证（见下节）。

> **历史实例（2026-09-16）**：记忆模块的 AutoDream 写入路径有三道安全闸（前缀闸 / delete 的
> `Source` 闸 / upsert 的 `Source` 闸）。初版删除守卫测试正是用 `manual:` 前缀 key，被前缀闸遮蔽，
> 属于**假绿**；改为非前缀 key 后，反证才真正失败。若不主动反证，该假绿测试会长期存在。

---

## 正确测试模式

### 断言必须验证"真实业务产出"

✅ **正确：**
```csharp
[Fact]
public async Task ExecuteAsync_EmptyPrompt_ReturnsError()
{
    var sut = new AgentTool(Substitute.For<IAgentRunner>());
    var input = CreateInput("""{"prompt":""}""");

    var result = await sut.ExecuteAsync(input, CreateContext());

    result.IsError.Should().BeTrue();                        // ← 业务产出：错误标记
    result.Content.Should().Contain("Error");                // ← 业务产出：错误消息内容
}
```

### 断言数值必须精确或有范围

✅ **正确：**
```csharp
tokens.Should().BeInRange(2, 6, "known short phrase should produce a small token count");
```

❌ **太弱：**
```csharp
tokens.Should().BeGreaterThan(0);  // 0 个 token 也满足，无意义
```

### 成本计算：验证每一步的明细

✅ **正确：**
```csharp
update.InputCost.Should().Be(3.00m);
update.OutputCost.Should().Be(15.00m);
update.CacheReadCost.Should().Be(1.50m);
update.TotalCost.Should().Be(19.50m);
update.CumulativeCost.Should().Be(update.TotalCost);
```

### 反证法：故意改错，确认测试会失败

守卫类逻辑（安全闸、排序、门控、过滤）写完测试后，**主动破坏被测逻辑一次**，确认测试确实失败，
再恢复。这是唯一能证明测试不是假绿的手段。

```csharp
// 1. 正常状态下运行 → 全绿
// 2. 临时把 PruneAsync 的 OrderByDescending 改成 OrderBy（即把淘汰顺序写反）
// 3. 重跑 → 三个淘汰守卫测试必须失败
// 4. 恢复 → 全绿 ⇒ 守卫真实有效
```

**适用范围**：多分支判定、排序/淘汰策略、门控条件、安全闸、格式解析契约。
**成本**：一次临时改动 + 一次局部测试运行，几分钟量级。
**回报**：本项目的三次重构中每次反证都有效，其中一次直接揭露了一处假绿测试（见上方第 8 类）。

```bash
# 反证时只跑相关测试即可，无需全量
dotnet test src/OneCode.Tests/OneCode.Tests.csproj --filter "FullyQualifiedName~MemoryEntryStoreTests"
```

### 契约测试：让写入路径与读取路径互相验证

跨越「存储 ↔ 消费」边界的逻辑（写文件 / 读文件、序列化 / 反序列化、产生事件 / 消费事件），
测试必须走**真实的一方**，不得手搓假数据。

❌ **禁止：** 自建目录、手写 JSON header，然后断言读取端能解析——这只证明了「我手写的假数据是我手写的」。
✅ **正确：** 用真实的写入端（如 `FileSessionEventStore`）产出数据，再让读取端消费。

> **历史实例（2026-09-15）**：AutoDream 的会话扫描目录曾与真实事件目录不一致（扫 `sessions/`，
> 实际写 `events/`），导致 AutoDream 从未真正运行。测试之所以一直通过，正是因为它们自建了
> `sessions/` 目录并手写 header，**把 BUG 完全掩盖**。改为走真实存储后立刻暴露。

### 对比性测试：验证合理的大小关系

✅ **正确：**
```csharp
var low = EffortThinking.GetThinkingBudget(EffortLevel.Low, "claude-sonnet-4-6");
var medium = EffortThinking.GetThinkingBudget(EffortLevel.Medium, "claude-sonnet-4-6");
low.Should().BeLessThan(medium);
```

### 权限/分类：验证具体的判定结果

✅ **正确：**
```csharp
[Theory]
[InlineData("rm -rf /", false)]
[InlineData("git status", true)]
public void IsReadOnly_CorrectlyClassifies(string command, bool expectedReadOnly)
{
    BashCommandClassifier.IsReadOnly(command).Should().Be(expectedReadOnly);
}
```

---

## 测试文件组织规范

### 一个文件测试一个被测类

`FooBarTests.cs` 对应 `FooBar.cs`。不要将多个被测类的测试混在一个文件中。

### 如果有重叠，删除而非容忍

当两个测试文件覆盖同一被测类且重叠超过 50% 时，保留测试质量更高或覆盖场景更全面的那个，**直接删除另一个**，不要试图"合并"或"保留以防万一"。重复的测试不仅浪费 CI 时间，还会让回归失败时排查范围翻倍。

### 文件路径

所有测试文件位于 `src/OneCode.Tests/` 根目录下（不使用子目录组织），确保与项目约定的扁平结构一致。

---

## 技术栈约束

| 维度 | 选型 | 禁止 |
|------|------|------|
| 测试框架 | xUnit v3 (`[Fact]` / `[Theory]`) | NUnit, MSTest |
| 断言 | FluentAssertions (`Should().Be/Contain/Throw`) | Assert.xxx() |
| Mock | NSubstitute (`Substitute.For<T>()`) | Moq, 手写 Stub |
| 数据驱动 | `[Theory]` + `[InlineData]` | 手写循环遍历 |

---

## 命名规范

| 约定 | 格式 | 示例 |
|------|------|------|
| 测试类名 | `{被测类}Tests` | `BudgetGuardRunMiddlewareTests` |
| 测试方法名 | `{方法名}_{场景}_{预期结果}` | `RunAsync_BudgetExceeded_ShortCircuitsWithoutCallingAgent` |
| 参数化测试 | `{方法名}_{场景描述}` | `ParseEffort_ReturnsCorrectLevel` |

---

## 不测试的内容

以下情况**不需要**编写自动化单元测试：

1. **纯数据模型**（POCO/DTO/Record）：没有行为的类型不需要测试
2. **DI 注册**（`services.AddXxx()`）：启动时的容器验证优于单元测试
3. **第三方库的封装层**（薄 wrapper）：测试第三方库本身不是你的责任
4. **日志输出**：验证 `logger.Received()` 没有防回归价值

---

## 快速自检清单

在提交测试代码前，逐条检查：

- [ ] 删除这个测试后，有没有业务回归无法被其他测试捕获？
- [ ] 断言的对象是被测方法的返回值，而不是我手动构造的变量？
- [ ] 数值断言有精确值或合理的上下界（不是 `> 0` / `!= null`）？
- [ ] 没有任何断言仅验证 Mock 对象的行为（`Received(1)`）而没有业务产出断言相伴？
- [ ] 如果有 `[InlineData]`，覆盖了至少一个边界条件（空值、极端值、无效值）？
- [ ] 测试名读起来像一个真实的业务场景，而不是一个方法调用描述？
- [ ] **守卫类测试：破坏它声称要验证的那道逻辑，这个测试会失败吗？**（不确定就做一次反证）
- [ ] **跨存储边界的测试：走的是真实写入端，还是我手搓的假数据？**

**如果任何一条不满足，该测试不应该被提交。**
