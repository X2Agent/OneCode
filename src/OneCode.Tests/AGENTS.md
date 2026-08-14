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
| 测试类名 | `{被测类}Tests` | `CostTrackerTests` |
| 测试方法名 | `{方法名}_{场景}_{预期结果}` | `RecordUsage_CalculatesCostCorrectly` |
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

**如果任何一条不满足，该测试不应该被提交。**
