# OneCode.Tests 单元测试规范

> **本文件是 AI 编码代理的强制约束。** 在对 `OneCode.Tests` 目录进行任何新增或修改之前，必须通读并遵守本文档中的所有规则。
> 通用编码规范见上级 [AGENTS.md](../AGENTS.md)。当本文档与上级文档冲突时，以本文档为准。

---

## 1. 单元测试的作用

单元测试是**回归防护网**，不是覆盖率数字的填充物。它承担三件事：

| 作用 | 含义 |
|------|------|
| 固化行为契约 | 把「代码应当做什么」写成可执行断言；契约被破坏时立刻失败 |
| 支撑重构 | 重构不必依赖人工回归——测试全绿即行为未变 |
| 暴露边界缺陷 | 空值、越界、并发、损坏输入等边界被显式覆盖 |

**唯一准入判据**：写出用例后问自己——**「如果这段代码回归了，这个测试能发现吗？」**
答案是「不能」，则该测试不得写入；已存在的应清理。

**测试不负责**：提升覆盖率数字、复述实现细节、替代文档、验证第三方库或编译器能检查的东西。

---

## 2. 必须删除/禁写的测试模式

以下模式**严禁新增**，已存在的应清理。

### 2.1 静态属性/常量验证

❌ 禁止：
```csharp
[Fact]
public void IsConfigured_IsAlwaysTrue()
{
    var provider = new DuckDuckGoSearchProvider(Substitute.For<IHttpClientFactory>());
    provider.Name.Should().Be("duckduckgo");
    provider.IsConfigured.Should().BeTrue();
}
```
验证固定的字符串/布尔属性。代码回归时编译器就会报错，这个测试不比编译检查多做任何事。

> 判定：断言右侧是**字面量**，且左侧取值不依赖任何输入 → 删除。

### 2.2 元数据结构验证

❌ 禁止：
```csharp
[Fact]
public void RenamedFunction_ExposesSchemaCopy()
{
    using var schema = JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"string"}}}""");
    var inner = Substitute.For<AIFunction>();
    inner.JsonSchema.Returns(schema.RootElement.Clone());
    var renamed = new RenamedAIFunction(inner, "mcp__server__original");

    renamed.JsonSchema.GetProperty("type").GetString().Should().Be("object");
}
```
Schema 结构是**测试自己喂进去的静态数据**。要验证的是「透传」这一行为，不是结构内容。

✅ 正确：
```csharp
renamed.JsonSchema.GetRawText().Should().Be(inner.JsonSchema.GetRawText(), "schema must pass through unchanged");
result.Should().Be("ok", "invocation must be delegated to the wrapped function");
```

### 2.3 初始状态检查

❌ 禁止：
```csharp
public void LastCacheSafeParams_StartsNull() { ... }
public void RegisteredTeams_Empty_ByDefault() { ... }
public void EmptyRegistry_GetAllTools_ReturnsEmpty() { ... }
```
对象的初始状态是构造函数的职责，不是业务逻辑。初始状态错了，依赖它的真实测试自然会失败。

### 2.4 元数据存取验证（写后立即读）

❌ 禁止：
```csharp
[Fact]
public void ForkedSession_MessagesContainForkRef()
{
    conv.Metadata["forkedFromSessionId"] = parentId;
    conv.Metadata["forkedFromSessionId"].Should().Be(parentId);
}
```
这是测试 `Dictionary` 的功能，不是测试业务代码。从来不会有回归要防。
判定：**同一测试内**写入后立刻读回同一个 key → 删除。（断言被测方法写入的结果是合规的。）

### 2.5 表达式断言（未调用被测方法）

❌ 禁止：
```csharp
[Fact]
public void MemoryExtract_TooFewMessages_Skips()
{
    var conv = CreateConversation(2);
    var shouldExtract = conv.Messages.Count >= 4;  // 自己写的表达式
    shouldExtract.Should().BeFalse();               // 断言自己写的那行
}
```
代码里根本没有 `>= 4` 这个表达式，这是把测试代码当被测代码。

### 2.6 反射计数 / 类型枚举

❌ 禁止：
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

### 2.7 纯 Mock 行为验证（无业务断言）

❌ 禁止：
```csharp
[Fact]
public async Task ExecuteAsync_StopsApplication()
{
    var lifetime = Substitute.For<IHostApplicationLifetime>();
    await cmd.ExecuteAsync([], ct);
    lifetime.Received(1).StopApplication();  // ← 唯一断言
}
```
实现改为调用两次仍通过，但业务可能已回归。

✅ 例外——**当 Mock 调用本身就是业务决定时**（调度选择、门控放行、server 解析、命令编排），
断言 Mock 交互是合法的，但必须满足两个条件：

1. **覆盖完整选择契约**：该分派的要断言分派，**不该分派的要断言未分派**；
2. 断言参数精确（`Arg.Is<string[]>(args => args.SequenceEqual([...]))`），不是宽泛的 `Arg.Any<T>()`。

```csharp
// ✅ 正确：due 工作流必须分派，未到期/会话未加载的不得分派 —— 一个完整的选择契约
await dispatcher.Received(1).StartBuildAsync(session, due, Arg.Any<CancellationToken>());
await dispatcher.DidNotReceive().StartBuildAsync(session, future, Arg.Any<CancellationToken>());
await dispatcher.DidNotReceive().StartBuildAsync(session, unloaded, Arg.Any<CancellationToken>());
```

### 2.8 被前置闸门遮蔽的守卫测试（假绿）

❌ 禁止：
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
本用例的 key 恰好带 `manual:` 前缀，**被第一道闸先拦下**，第二道闸无论是否被破坏，测试都会通过。
也就是说：把这个测试当作 `Source` 守卫的证明是**假的**。删掉第二道闸，它依然全绿。

✅ 正确：每条守卫都需要一个能绕过前面所有闸门的用例
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

**判定方法**：写出用例后问自己——「破坏我真正想验证的那道闸，这个测试会失败吗？」不确定就去做一次反证（见 §3.4）。

### 2.9 静态数据镜像 / 路由映射表镜像

❌ 禁止：
```csharp
[Theory]
[InlineData("definition", "textDocument/definition")]
[InlineData("hover", "textDocument/hover")]
public async Task ExecuteLspAsync_Action_CallsExpectedMethod(string action, string expectedMethod)
{
    var manager = CreateManagerWithServer();   // mock 固定返回 null
    var result = await sut.ExecuteLspAsync(action, "test.cs", server: "test-server", ct: ct);

    // 恒真断言：字段来自「null 响应的默认包装结构」，与 action 无关
    result.Content.Should().Contain("locations");
    await manager.Received(1).SendRequestAsync("test-server", expectedMethod, ...);
}
```
两个问题叠加：① 「action → 方法名」只是把实现里的 `switch` 表抄了一遍；② 业务断言落在
**null 响应的默认结构**上，任何 action 都会通过。

✅ 正确：让 Mock 返回**真实响应**，断言响应被透传；路由断言作为辅助
```csharp
using var responseDoc = JsonDocument.Parse("""{"payload":"from-server"}""");
manager.SendRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
    .Returns(Task.FromResult<JsonElement?>(responseDoc.RootElement.Clone()));

var result = await sut.ExecuteLspAsync(action, "test.cs", line: 5, column: 10, server: "test-server", ct: ct);

result.IsError.Should().BeFalse();
result.Content.Should().Contain("from-server", "the server response must pass through unchanged");
await manager.Received(1).SendRequestAsync("test-server", expectedMethod, Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
```

**同类形态**（同样禁止）：
- **快照镜像**：逐条断言模型上下文窗口表、默认值表（`Should().Be(1_047_576)` 之类）。
  只保留每个 provider 一个代表用例以验证查表链路走通；穷举表内容无逻辑覆盖价值。
- **透传镜像**：断言 `wrapper.Name == 构造参数`、`clone.Field == 原对象.Field`。
  这类断言只有在**验证传递函数**（深拷贝保真、包装器透传）时才有价值，且应断言到关键字段而非全字段清单。

**例外**：两张表存在**派生关系**（单一事实源）时，一致性断言有防漂移价值。
例如 `BashCommandInvariant.DenyPatternStrings` 派生自 `DangerousCommandPatterns.Layer0HardDeny`，
断言二者等价可以防止未来回退到「分别声明、容易漂移」的写法。

---

## 3. 正确测试模式

### 3.1 断言必须验证"真实业务产出"

✅ 正确：
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

### 3.2 断言数值必须精确或有范围（弱断言禁令）

以下断言**不得单独出现**，必须改造为精确值或有意义的上下界：

| 弱断言 | 问题 | 改造方向 |
|--------|------|----------|
| `Should().BeGreaterThan(0)` | `1` 也满足，无法发现少算/算错 | 精确值；或 `BeInRange(low, high, "理由")`，上下界都要有语义 |
| `Should().NotBeNull()` | 只验证对象存在，不验证可用 | 断言具体类型、关键能力或后续可用行为 |
| `Should().NotThrowAsync()` | 不抛异常 ≠ 行为正确 | 断言操作后的状态契约（如「未知会话释放后管理器仍可用」） |
| 单独 `Should().BeOfType<T>()` | 只验证类型 | 追加产出/状态断言 |
| 前置条件用 `> 0` | 前置条件自身无回归价值 | 保留但改为有意义下界：`BeGreaterThanOrEqualTo(2, "标题与选项行")` |

✅ 正确：
```csharp
// 精确值：token 估算 "Hello world" = ceil(11 / 4) 文本 token + 12 消息开销
result.EstimatedInputTokens.Should().Be(15);

// 有界：单个带 schema 的工具必须产生有限开销（上界防高估，下界防漏算）
singleToolBreakdown.ToolsAndSkills.Should().BeInRange(20, 60);

// 精确增量：系统提示词按字符加权估算的确定性增量
withPrompt.EstimatedInputTokens.Should().Be(withoutPrompt.EstimatedInputTokens + 7);
```

> **名实相符**：测试方法名承诺什么，就必须断言什么。
> `GetMaxContextTokens_WithRegistry_ReturnsCatalogValue` 只写 `BeGreaterThan(0)`、
> 或用例实际走的是回退路径，都属名实不符，必须改名或改断言。

### 3.3 成本计算：验证每一步的明细

✅ 正确：
```csharp
update.InputCost.Should().Be(3.00m);
update.OutputCost.Should().Be(15.00m);
update.CacheReadCost.Should().Be(1.50m);
update.TotalCost.Should().Be(19.50m);
update.CumulativeCost.Should().Be(update.TotalCost);
```

### 3.4 反证法：故意改错，确认测试会失败

守卫类逻辑（安全闸、排序、门控、过滤、状态机）写完测试后，**主动破坏被测逻辑一次**，
确认测试确实失败，再恢复。这是唯一能证明测试不是假绿的手段。

```csharp
// 1. 正常状态下运行 → 全绿
// 2. 临时把 PruneAsync 的 OrderByDescending 改成 OrderBy（即把淘汰顺序写反）
// 3. 重跑 → 三个淘汰守卫测试必须失败
// 4. 恢复 → 全绿 ⇒ 守卫真实有效
```

**适用范围**：多分支判定、排序/淘汰策略、门控条件、安全闸、格式解析契约。
**成本**：一次临时改动 + 一次局部测试运行。

```bash
# 反证时只跑相关测试即可，无需全量
dotnet test src/OneCode.Tests/OneCode.Tests.csproj --filter "FullyQualifiedName~MemoryEntryStoreTests"
```

### 3.5 契约测试：让写入路径与读取路径互相验证

跨越「存储 ↔ 消费」边界的逻辑（写文件 / 读文件、序列化 / 反序列化、产生事件 / 消费事件），
测试必须走**真实的一方**，不得手搓假数据。

❌ 禁止：自建目录、手写 JSON header，然后断言读取端能解析——这只证明了「我手写的假数据是我手写的」。
✅ 正确：用真实的写入端（如 `FileSessionEventStore`）产出数据，再让读取端消费。

> 自动扫描目录、恢复流程等特性尤其容易踩这个坑：目录名写错时，
> 自建假数据的测试会**掩盖 BUG**，走真实存储的测试才能立刻暴露。

### 3.6 对比性测试：验证合理的大小关系

✅ 正确：
```csharp
var low = EffortThinking.GetThinkingBudget(EffortLevel.Low, "claude-sonnet-4-6");
var medium = EffortThinking.GetThinkingBudget(EffortLevel.Medium, "claude-sonnet-4-6");
low.Should().BeLessThan(medium);
```

### 3.7 权限/分类：数据驱动验证具体判定结果

✅ 正确：
```csharp
[Theory]
[InlineData("rm -rf /", false)]
[InlineData("git status", true)]
public void IsReadOnly_CorrectlyClassifies(string command, bool expectedReadOnly)
{
    BashCommandClassifier.IsReadOnly(command).Should().Be(expectedReadOnly);
}
```

### 3.8 同一语义存在多份映射时，断言实际生效的那一份

当实现里出现「同一映射写了两遍」（例如 LSP action 既在能力门控处查表、又在分派处硬编码方法名），
测试必须断言**实际生效的那一份**——即真正发出去的方法名/参数，而不是门控侧的查表结果。

只断言门控侧的表，实际分派被改错不会被任何测试发现；反之亦然。
写完后按 §3.4 做一次反证：把实际分派改错，确认测试变红。

---

## 4. 测试文件组织

### 4.1 一个文件测试一个被测类

`FooBarTests.cs` 对应 `FooBar.cs`。一个文件里混装多个测试类必须拆分——
类名之间没有语义关联的「桶名」文件（如 `XxxAndYyyTests.cs` 内含多个类）尤其要拆。

### 4.2 如果有重叠，删除而非容忍

当两个测试文件覆盖同一被测类且重叠超过 50% 时，保留测试质量更高或覆盖场景更全面的那个，
**直接删除另一个**，不要试图"合并"或"保留以防万一"。重复的测试不仅浪费 CI 时间，
还会让回归失败时排查范围翻倍。

### 4.3 跨模块平行测试不得误删

**同名/同义用例 ≠ 重复**。删除前必须核实：**这些用例调用的是不是同一个生产方法？**

```text
GoalWorkflowTests / ControlledBuildAttemptWorkflowTests / TeamTaskWorkflowTests
  各自覆盖 GoalWorkflowCompiler / ControlledBuildAttemptWorkflowCompiler /
  TeamTaskWorkflowCompiler 三份独立实现（contract 标记不同），
  测试名相同但被测类不同 → 全部保留。
```

判定步骤：
1. 打开用例，找到它调用的生产方法（LSP 定义跳转）；
2. 确认两个用例是否命中同一个类；
3. 只有「同一个类 + 场景重叠 > 50%」才允许删除其一。

### 4.4 文件路径与长度

- 所有测试文件位于 `src/OneCode.Tests/` 根目录下（不使用子目录组织），与项目约定的扁平结构一致。
- 单个测试文件不得超过 **700 行**（见上级规范 §11.1）。接近上限时按被测类拆分，而不是继续追加。

---

## 5. 技术栈约束

| 维度 | 选型 | 禁止 |
|------|------|------|
| 测试框架 | xUnit v3 (`[Fact]` / `[Theory]`) | NUnit, MSTest |
| 断言 | FluentAssertions (`Should().Be/Contain/Throw`) | `Assert.xxx()` |
| Mock | NSubstitute (`Substitute.For<T>()`) | Moq, 手写 Stub |
| 数据驱动 | `[Theory]` + `[InlineData]` | 手写循环遍历 |
| 取消令牌 | `TestContext.Current.CancellationToken` | 手写 `CancellationToken.None` |

**xUnit v3 注意事项：**
- `JsonDocument` 实现 `IDisposable`，测试中必须 `using var doc = JsonDocument.Parse(...)`；
  需要跨作用域使用时先 `.RootElement.Clone()`。

---

## 6. 命名规范

| 约定 | 格式 | 示例 |
|------|------|------|
| 测试类名 | `{被测类}Tests` | `BudgetGuardRunMiddlewareTests` |
| 测试方法名 | `{方法名}_{场景}_{预期结果}` | `RunAsync_BudgetExceeded_ShortCircuitsWithoutCallingAgent` |
| 参数化测试 | `{方法名}_{场景描述}` | `ParseEffort_ReturnsCorrectLevel` |

方法名读起来必须像一个真实的业务场景，而不是一个方法调用描述。
参数命名同理：`due` / `future` / `unloaded` 好过 `w1` / `w2` / `w3`。

---

## 7. 不测试的内容

1. **纯数据模型**（POCO/DTO/Record）：没有行为的类型不需要测试
2. **DI 注册**（`services.AddXxx()`）：启动时的容器验证优于单元测试（DI 面快照护栏见上级规范）
3. **第三方库的封装层**（薄 wrapper）：测试第三方库本身不是你的责任
4. **日志输出**：验证 `logger.Received()` 没有防回归价值
5. **编译器/类型系统能检查的东西**：固定属性值、接口实现、可见性

---

## 8. 快速自检清单

提交测试代码前逐条检查：

- [ ] 删除这个测试后，有没有业务回归无法被其他测试捕获？
- [ ] 断言的对象是被测方法的返回值/产出，而不是我手动构造的变量或 Mock 调用记录？
- [ ] 数值断言有精确值或合理的上下界（不是 `> 0` / `!= null`）？上下界都写了理由？
- [ ] 测试方法名与断言内容名实相符（没有承诺 catalog 值却只断言 `> 0`）？
- [ ] 没有断言仅验证 Mock 对象的行为而没有业务产出断言相伴？若有，是否覆盖了「不该分派」的反面场景？
- [ ] 如果有 `[InlineData]`，覆盖了至少一个边界条件（空值、极端值、无效值）？
- [ ] 测试名读起来像一个真实的业务场景，而不是一个方法调用描述？
- [ ] **守卫类测试：破坏它声称要验证的那道逻辑，这个测试会失败吗？**（不确定就做一次反证）
- [ ] **跨存储边界的测试：走的是真实写入端，还是我手搓的假数据？**
- [ ] **断言的是行为还是静态数据镜像？**（表镜像、路由映射镜像、快照穷举一律不得写入）
- [ ] 这个文件只有一个被测类？总行数没超过 700？

**如果任何一条不满足，该测试不应该被提交。**
