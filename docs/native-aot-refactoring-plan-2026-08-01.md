# OneCode Native AOT 支持重构方案

- 文档状态：Draft
- 编写日期：2026-08-01
- 适用范围：当前工作区 `C:\Users\mayue\Desktop\ClaudeCode`
- 目标项目：`src/OneCode.Cli/OneCode.Cli.csproj`
- 目标框架：`.NET 10 / net10.0`
- 基线结论：普通 self-contained single-file + ReadyToRun 可发布；Native AOT 当前无法通过编译

---

## 1. 结论与决策

### 1.1 当前结论

OneCode 已具备独立的 Native AOT 构建入口，但主体代码尚未完成 AOT 化，当前不能将 Native AOT 视为受支持的发布方式。

当前能力分级：

| 能力 | 状态 | 说明 |
|---|---|---|
| Release Build | 已支持 | 解决方案可正常构建 |
| Framework-dependent Publish | 已支持 | 非本方案重点 |
| Self-contained Publish | 已支持 | 正式 Release 当前采用 |
| Single-file | 部分支持 | 主程序可单文件，但 Playwright、prompt、YAML、native runtime 仍需外部资源 |
| ReadyToRun | 已支持 | 正式 Release 已启用 |
| Trimming | 未支持 | 正式发布明确关闭；启用后存在 IL2026/IL2091/IL2104 等风险 |
| Native AOT | 未支持 | `win-x64` 实测在 `OneCode.Core` 阶段失败 |
| AOT CI 门禁 | 未建立 | Release workflow 未执行 AOT 构建 |
| AOT 运行时验证 | 未建立 | 尚无 AOT 产物和完整功能冒烟测试 |

生产决策：

1. 在本方案全部验收前，正式 Release 继续使用 `self-contained + single-file + ReadyToRun + PublishTrimmed=false`。
2. `AotPublish` 保持实验入口，不进入正式 Tag Release 主链路。
3. 不允许通过批量压制 IL 告警来制造“编译通过”；每一项压制都必须有可证明的静态保留边界和运行时测试。
4. 优先删除运行时反射和任意对象序列化，而不是用 linker descriptor 大面积保留程序集。
5. AOT 改造采用“逐项目清零”策略：`Core → Infrastructure → Automation → App → Cli → 第三方依赖 → CI`。

### 1.2 成功定义

Native AOT 只有同时满足以下条件，才可标记为正式支持：

- `win-x64`、`linux-x64` 至少两个 RID 可稳定 AOT 发布；正式全平台目标见第 11 节。
- AOT 发布过程对仓库代码达到 0 个未审计的 `IL2xxx/IL3xxx` 告警。
- 不依赖 `JsonSerializerIsReflectionEnabledByDefault=true`。
- 不依赖以字符串查找业务方法的工具注册机制。
- AOT 产物通过启动、配置、Session、工具调用、MCP、Build/Team/Goal、TUI、Playwright/WebFetch 等冒烟测试。
- CI 中 AOT build 和 smoke test 是强制门禁。
- README、构建脚本、项目约束和实际配置一致。

---

## 2. 当前工程与发布基线

### 2.1 工程依赖方向

```text
OneCode.Cli (Exe)
  └─ OneCode.App
       ├─ OneCode.Core
       ├─ OneCode.Infrastructure
       │    └─ OneCode.Core
       └─ OneCode.Automation
            ├─ OneCode.Core
            └─ OneCode.Infrastructure
```

解决方案：`src/OneCode.slnx`

项目：

1. `src/OneCode.Core/OneCode.Core.csproj`
2. `src/OneCode.Infrastructure/OneCode.Infrastructure.csproj`
3. `src/OneCode.Automation/OneCode.Automation.csproj`
4. `src/OneCode.App/OneCode.App.csproj`
5. `src/OneCode.Cli/OneCode.Cli.csproj`
6. `src/OneCode.Tests/OneCode.Tests.csproj`

统一配置 `src/Directory.Build.props:8-39`：

- `TargetFramework=net10.0`
- `LangVersion=latest`
- Nullable、ImplicitUsings、代码分析启用
- 全局 `TreatWarningsAsErrors=false`

### 2.2 正式发布配置

正式 Tag Release 位于 `.github/workflows/release.yml:87-103`，当前参数为：

```text
--self-contained true
PublishTrimmed=false
PublishSingleFile=true
PublishReadyToRun=true
IncludeNativeLibrariesForSelfExtract=true
EnableCompressionInSingleFile=true
```

这是自包含、单文件、ReadyToRun 发布，不是 Native AOT。

`src/OneCode.Cli/OneCode.Cli.csproj:7-16` 明确记录当前技术债务：

```xml
<PublishTrimmed>false</PublishTrimmed>
<JsonSerializerIsReflectionEnabledByDefault>true</JsonSerializerIsReflectionEnabledByDefault>
<EnableTrimAnalyzer>false</EnableTrimAnalyzer>
<EnableAotAnalyzer>false</EnableAotAnalyzer>
```

### 2.3 实验性 AOT 入口

`scripts/build.ps1:115-144` 已提供正确方向的实验入口：

```text
PublishAot=true
PublishTrimmed=true
TrimMode=full
JsonSerializerIsReflectionEnabledByDefault=false
EnableTrimAnalyzer=true
EnableAotAnalyzer=true
```

该入口应保留，作为改造过程中的唯一 AOT 验证入口；不要让普通 `dotnet build` 隐式承担 AOT 发布语义。

---

## 3. 复现结果与已确认阻塞

### 3.1 复现命令

```powershell
dotnet publish src/OneCode.Cli/OneCode.Cli.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishAot=true `
  -p:PublishTrimmed=true `
  -p:TrimMode=full `
  -p:JsonSerializerIsReflectionEnabledByDefault=false `
  -p:EnableTrimAnalyzer=true `
  -p:EnableAotAnalyzer=true `
  -o .workbuddy/aot-audit-current
```

实测结果：退出码 1，在 `OneCode.Core` 编译阶段失败。当前 SDK 为 `.NET 10 preview`，同时出现 `NETSDK1057`；此提示不是本次 AOT 失败根因，但会影响构建可复现性。

### 3.2 首批 11 个错误

| 文件 | 行号 | 错误 | 根因 |
|---|---:|---|---|
| `OneCode.Core/Tools/ToolServiceCollectionExtensions.cs` | 31 | IL2091 | `AddTool<T>` 的 `T` 未声明 DI 所需公共构造函数保留要求 |
| `OneCode.Core/Keybindings/KeybindingSchema.cs` | 70 | IL2026、IL3050 | 反射式 `JsonSerializer.Serialize` |
| 同上 | 99 | IL2026、IL3050 | 反射式 `JsonSerializer.Serialize` |
| `OneCode.Core/Tools/ToolArgumentExtractor.cs` | 125 | IL2026、IL3050 | 对任意 `object` 进行反射式 JSON 序列化 |
| `OneCode.Core/Tools/ToolResultSerializer.cs` | 35 | IL2026、IL3050 | `ToolResult` 未使用生成的 `JsonTypeInfo` |
| `OneCode.Core/Tools/ToolResult.cs` | 33 | IL2026、IL3050 | `JsonSuccess(object)` 依赖运行时对象类型 |

### 3.3 第一层错误之后的预期风险

当前失败发生在最底层 `OneCode.Core`。修复这些错误以后，分析器会继续进入 Infrastructure、Automation、App 和第三方程序集，预计将暴露：

- 大量未使用 `JsonSerializerContext` 的 JSON 路径。
- `ToolCatalog` 的方法反射和 `AIFunctionFactory.Create(MethodInfo)`。
- Terminal.Gui Kitty Keyboard 兼容代码的反射实例化和调用。
- YamlDotNet、MAF、MCP、Terminal.Gui、Playwright、Hyperlight 等第三方包的 trim/AOT 告警。
- 多态 Session、MAF 内容对象、`Dictionary<string, object?>`、匿名对象等难以静态建模的序列化边界。

因此，不能把“修复当前 11 个错误”理解为“AOT 改造完成”。

---

## 4. 设计原则

### 4.1 强类型边界优先

AOT 的核心不是增加标注，而是让编译器能够静态确定：

- 会创建哪些类型；
- 会调用哪些方法；
- 会序列化哪些数据契约；
- 必须保留哪些成员。

因此优先级为：

1. 强类型 API；
2. 编译期源生成；
3. 精确的 `DynamicallyAccessedMembers` / `DynamicDependency`；
4. 小范围 linker descriptor；
5. 禁止使用“保留整个程序集”和全局 suppress 作为默认方案。

### 4.2 JSON Context 按程序集归属

不要建立一个包含全仓数百类型的超级 `OneCodeJsonContext`。建议每个程序集维护自己的序列化契约：

```text
OneCode.Core/Serialization/CoreJsonContext.cs
OneCode.Infrastructure/Serialization/InfrastructureJsonContext.cs
OneCode.Automation/Serialization/AutomationJsonContext.cs
OneCode.App/Serialization/AppJsonContext.cs
```

理由：

- 避免 Core 反向依赖上层 DTO；
- 控制生成代码规模；
- 便于逐项目启用 `IsAotCompatible`；
- 清晰区分持久化契约、协议契约和 UI 临时 DTO。

### 4.3 不序列化任意对象

以下签名属于 AOT 反模式：

```csharp
string Serialize(object value)
ToolResult JsonSuccess(object data)
Dictionary<string, object?>
JsonSerializer.Serialize(value, value.GetType(), options)
```

替代策略：

- 已知 DTO：接收 `JsonTypeInfo<T>`。
- 动态 JSON：直接构造 `JsonObject`、`JsonArray`、`JsonElement` 或使用 `Utf8JsonWriter`。
- 工具参数：规范化成 `JsonElement`，不要反序列化回任意 CLR 对象。
- 多态消息：显式 `[JsonPolymorphic]` / `[JsonDerivedType]` 或自定义源生成兼容 converter。

### 4.4 工具注册不得依赖字符串方法名

当前 `ToolRegistration` 保存：

```text
Type ServiceType
string MethodName
bool IsStatic
```

`ToolCatalog` 再通过 `GetMethod` 找到方法。这会同时造成：

- trim 后方法可能被移除；
- 重命名只在运行时失败；
- 重载解析不明确；
- AOT 分析器无法证明调用边界。

目标设计应让每个工具注册直接携带创建 `AIFunction` 的强类型工厂，不再保存 `MethodName`。

---

## 5. 目标架构

### 5.1 AOT 配置分层

建议新增 `src/Directory.Build.targets`，只负责分析器策略，不把所有库项目都变成可执行发布项目。

建议配置语义：

```xml
<Project>
  <PropertyGroup>
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
    <EnableAotAnalyzer>true</EnableAotAnalyzer>
  </PropertyGroup>

  <PropertyGroup Condition="'$(AotAudit)' == 'true'">
    <WarningsAsErrors>$(WarningsAsErrors);IL2026;IL2067;IL2070;IL2072;IL2075;IL2091;IL2104;IL3050</WarningsAsErrors>
  </PropertyGroup>
</Project>
```

注意：

- 具体错误码清单应根据最新构建结果维护；更稳妥的最终状态是 AOT CI 将所有 linker/AOT 告警视为错误。
- `PublishAot` 仅在 `OneCode.Cli` 发布或命令行显式传入时启用。
- 库项目在达到兼容要求后逐一添加 `<IsAotCompatible>true</IsAotCompatible>`，不要提前虚假声明。

### 5.2 强类型工具注册

建议将 `ToolRegistration` 收敛为：

```csharp
public sealed record ToolRegistration(
    string Name,
    ToolRisk Risk,
    Func<IServiceProvider, AIFunction> FunctionFactory,
    IReadOnlyList<string>? Aliases = null,
    bool Concurrency = true,
    bool Visible = true,
    string? SearchHint = null,
    ToolApprovalMode? ApprovalMode = null,
    ToolLoadPolicy LoadPolicy = ToolLoadPolicy.Always,
    IReadOnlyList<string>? Keywords = null,
    ToolCategory Category = ToolCategory.None);
```

工具注册直接表达委托：

```csharp
services.AddSingleton<BashTool>();
services.AddTool(
    name: "Bash",
    risk: ToolRisk.Dynamic,
    functionFactory: sp =>
    {
        var tool = sp.GetRequiredService<BashTool>();
        return AIFunctionFactory.Create(tool.ExecuteAsync, name: "Bash");
    });
```

如果 MAF 的委托重载仍触发动态代码生成，则应进一步采用 MAF 支持的 source-generated schema/API；不能退回 `MethodInfo` 反射路径。

### 5.3 JSON 契约架构

每个 Context 应明确包含：

- 持久化文件契约；
- 对外协议 DTO；
- 工具输入输出 DTO；
- 多态派生类型；
- 必要集合类型。

示例：

```csharp
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ToolResult))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
internal partial class CoreJsonContext : JsonSerializerContext;
```

使用方式：

```csharp
return JsonSerializer.Serialize(result, CoreJsonContext.Default.ToolResult);
```

对于需要不同格式策略的同一 DTO，使用多个 Context 或通过 resolver chain 配置，不要在业务代码中随处创建 `JsonSerializerOptions`。

---

## 6. 分阶段实施计划

## Phase 0：固定基线与文档一致性

### 目标

建立可重复的 AOT 失败基线，避免改造期间因 SDK、依赖和文档漂移造成噪声。

### 任务

1. 添加 `global.json`，固定团队认可的 .NET 10 SDK。
2. 将 `Directory.Packages.props` 中 `10.*`、`1.*`、`3.*` 等浮动版本改为精确版本。
3. 修正以下文档冲突：
   - `src/AGENTS.md:56` 声称 CLI 已启用 AOT Analyzer；实际默认关闭。
   - `src/OneCode.Cli/AGENTS.md:10` 声称默认启用 `PublishTrimmed + EnableAotAnalyzer + TrimMode=partial`；实际正式发布关闭 trimming，实验入口使用 `full`。
   - `src/OneCode.Cli/AGENTS.md:91-92` 的普通 `dotnet publish` 不能代表 AOT 发布测试。
4. 固化基线命令和日志采集方式。
5. 增加 AOT 审计配置，但暂不接入正式 Release。

### 验收

- 任意开发机和 CI 使用同一 SDK/依赖版本。
- 文档、csproj、构建脚本的 AOT 语义一致。
- 基线 AOT 命令可稳定复现相同的首批错误。

---

## Phase 1：清零 OneCode.Core AOT 错误

### 目标

使 `OneCode.Core` 在 AOT Analyzer 下 0 warning、0 error，并声明 `IsAotCompatible=true`。

### 1.1 修复 DI 泛型标注

文件：`src/OneCode.Core/Tools/ToolServiceCollectionExtensions.cs:17-31`

短期修复：

```csharp
public static IServiceCollection AddTool<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(...)
    where T : class
```

但该修复只解决 DI 构造函数保留，不解决后续工具方法反射。Phase 3 仍须删除 `ServiceType + MethodName` 机制。

测试：

- 每个工具类型可由 DI 创建。
- 无公共构造函数的工具应在编译或注册测试阶段失败，而不是运行时失败。

### 1.2 新建 CoreJsonContext

建议文件：`src/OneCode.Core/Serialization/CoreJsonContext.cs`

首批覆盖：

- `ToolResult`
- ToolResult 中可接受的 Telemetry 值模型
- Keybinding schema/template 所需 JSON DOM 类型
- 明确需要跨层共享的基础 DTO

### 1.3 重构 KeybindingSchema

文件：`src/OneCode.Core/Keybindings/KeybindingSchema.cs:64-99`

当前问题：使用 `Dictionary<string, object>` 构造 schema，再进行反射式序列化。

推荐方案：直接使用 `JsonObject` / `JsonArray` 构建 schema 和模板。

理由：这是天然的动态 JSON 文档，不应该映射成任意 CLR object graph。`JsonNode.ToJsonString(options)` 不需要发现未知业务类型。

验收：

- 生成内容与现有快照语义一致。
- `$schema` URI、bindings、enum、required、additionalProperties 完整保留。
- AOT Analyzer 不产生 IL2026/IL3050。

### 1.4 重构 ToolArgumentExtractor

文件：`src/OneCode.Core/Tools/ToolArgumentExtractor.cs:116-134`

当前问题：fallback 对任意 `arguments` 调用 `JsonSerializer.Serialize(arguments)`。

目标契约：权限层只接受下列输入：

- `IReadOnlyDictionary<string, object?>` 中的已知 primitive / `JsonElement`；
- `JsonElement`；
- 可选的强类型 `ToolArguments` 接口。

建议删除任意对象 fallback。无法识别时：

- 返回“无法提取路径”的结构化结果；
- 权限系统采用保守策略，不得静默降低权限检查强度；
- 记录工具名、参数运行时类型和 trace id，但不得序列化整个未知对象。

### 1.5 重构 ToolResult.JsonSuccess

文件：`src/OneCode.Core/Tools/ToolResult.cs:28-33`

删除：

```csharp
JsonSuccess(object data)
```

替代 API 二选一：

```csharp
public static ToolResult JsonSuccess(JsonElement data, ...)
public static ToolResult JsonSuccess(JsonNode data, ...)
```

或：

```csharp
public static ToolResult JsonSuccess<T>(T data, JsonTypeInfo<T> typeInfo, ...)
```

推荐同时提供两种：JSON DOM 处理真正动态结果，`JsonTypeInfo<T>` 处理强类型 DTO。

### 1.6 重构 ToolResultSerializer

文件：`src/OneCode.Core/Tools/ToolResultSerializer.cs:18-35`

改为：

```csharp
JsonSerializer.Serialize(result, CoreJsonContext.Default.ToolResult)
```

Telemetry 的 `object?` 值必须收窄。建议定义允许类型：

- `string`
- `bool`
- `long`
- `double`
- `decimal`
- `DateTimeOffset`
- `JsonElement`

如必须保持 `object?`，需提供显式 converter，禁止回退到反射 resolver。

### Phase 1 验收命令

```powershell
dotnet build src/OneCode.Core/OneCode.Core.csproj -c Release `
  -p:EnableTrimAnalyzer=true `
  -p:EnableAotAnalyzer=true `
  -p:AotAudit=true
```

验收标准：0 warning、0 error，相关测试全绿，然后才添加：

```xml
<IsAotCompatible>true</IsAotCompatible>
```

---

## Phase 2：JSON 全仓源生成改造

### 目标

在关闭反射序列化时，所有一方代码 JSON 路径仍完整工作。

### 2.1 InfrastructureJsonContext

建议优先处理以下文件：

| 模块 | 文件 | 主要契约 |
|---|---|---|
| Build | `Infrastructure/Build/JsonBuildRunStore.cs` | BuildRun、BuildRunEvent、列表/快照 |
| Goals | `Infrastructure/Goals/DiskGoalCheckpointStore.cs` | Goal checkpoint/envelope |
| Tasks | `Infrastructure/Tasks/JsonTaskStore.cs` | TaskSnapshotEnvelope |
| Teams | `Infrastructure/Teams/JsonTeamRunStore.cs` | TeamRun |
| MCP | `Infrastructure/Mcp/McpRegistryClient.cs` | RegistrySearchResult、RegistryServer |
| MCP WS | `Infrastructure/Mcp/WebSocketClientTransport.cs` | JsonRpcMessage |
| Config | `Infrastructure/Config/ConfigManager.cs` | 配置 DTO/JSON DOM |
| Keybindings | `Infrastructure/Keybindings/KeybindingLoader.cs` | KeybindingsConfig |
| Permissions | `Infrastructure/Permissions/Yolo/YoloRuleFileStore.cs` | `List<UserRule>` |
| AI/VCR | `Infrastructure/Ai/VcrChatClientDecorator.cs`、`VcrDelegatingHandler.cs` | VCR fixture、ChatResponseUpdate 集合 |

要求：

- 每个持久化文件格式必须有 round-trip 和旧版本兼容测试。
- 不允许用 `object`/匿名对象绕过 Context。
- 对 MAF 类型不能直接生成时，建立 OneCode 自有持久化 DTO，在边界处显式映射。

### 2.2 AutomationJsonContext

文件：

- `OneCode.Automation/Cron/CronTools.cs`
- `OneCode.Automation/Cron/CronSchedulerService.cs`

将匿名对象输出改为明确 DTO 或 `JsonObject`；将 `CronJobEntry` 注册到 Context。Cron 持久化需要版本字段和兼容测试。

### 2.3 AppJsonContext

重点：

- `Session/SessionStore.cs`
- `Session/ConversationMessageMapper.cs`
- `Commands/ExportCommand.cs`
- `Commands/McpCommand.cs`
- `Services/GoalMode/GoalExecutionState.cs`
- `Services/Agent/GoalDecomposer.cs`
- `Services/AutoDream/AutoDreamService.cs`
- `Services/PlanMode/PlanArtifactStore.cs`
- `Services/PlanMode/PlanWorkflowStore.cs`
- `Services/Lsp/*`
- `Tools/*` 中的匿名 JSON 输出

### 2.4 SessionStore 多态改造

`src/OneCode.App/Session/SessionStore.cs:91-97` 当前按 `msg.GetType()` 序列化；`298-303` 又按消息类型字段分支反序列化。

推荐建立稳定持久化 envelope：

```csharp
internal sealed record SessionMessageEnvelope(
    string Type,
    int Version,
    JsonElement Payload);
```

写入时由显式模式匹配选择对应 `JsonTypeInfo`：

```csharp
var payload = message switch
{
    UserMessage value => JsonSerializer.SerializeToElement(value, AppJsonContext.Default.UserMessage),
    AssistantMessage value => JsonSerializer.SerializeToElement(value, AppJsonContext.Default.AssistantMessage),
    _ => throw new NotSupportedException(...)
};
```

读取时按 `Type` 显式分派。禁止继续使用 `GetType()` 作为序列化契约选择机制。

必须覆盖：

- 现有 session 文件回读；
- 新旧格式迁移；
- 未知消息类型的错误行为；
- Text/ToolUse/Thinking/RedactedThinking block 多态；
- round-trip 后消息顺序、usage、metadata 不丢失。

### Phase 2 验收

1. 测试环境设置：

```text
JsonSerializerIsReflectionEnabledByDefault=false
```

2. 所有 JSON round-trip 测试通过。
3. 仓库一方代码不再存在未审计的反射式 `JsonSerializer.Serialize/Deserialize` 调用。
4. 禁止通过在 options 中重新加入 `DefaultJsonTypeInfoResolver` 恢复反射。

---

## Phase 3：删除工具方法运行时反射

### 目标

消除 `ToolRegistration.ServiceType + MethodName` 和 `ToolCatalog.GetMethod`。

### 涉及文件

- `OneCode.Core/Tools/ToolRegistration.cs:13-28`
- `OneCode.Core/Tools/ToolServiceCollectionExtensions.cs:17-67`
- `OneCode.App/Tools/ToolCatalog.cs:142-168`
- `OneCode.App/Tools/ToolRegistrationExtensions.cs`
- `OneCode.App/ServiceCollectionExtensions.Tools.cs`
- `OneCode.Automation/ServiceCollectionExtensions.cs`

### 实施步骤

1. 扩展现有 `AddToolInstance` 思路，使所有工具统一使用 `FunctionFactory`。
2. 为常见工具签名提供少量强类型 helper；不要引入复杂继承层级。
3. 逐个迁移约 30+ 个 `AddTool<T>(name, nameof(...))` 调用。
4. 删除 `ServiceType`、`MethodName`、`IsStatic`、`InstanceFactory` 字段。
5. 删除 `ToolCatalog.CreateFunction` 的反射分支。
6. 增加注册完整性测试：工具名称唯一、工厂可解析、Schema 可生成、风险元数据完整。

### 关键验证

- 工具方法重命名必须产生编译错误，而不是运行时 `Tool method not found`。
- 每个工具在 AOT 产物中可创建并执行。
- MAF function schema 生成无 IL3050；如第三方 API 本身不支持 AOT，必须在此阶段明确阻断并寻找官方 AOT API，而不是 suppress。

---

## Phase 4：清理剩余反射与动态路径

### 4.1 Terminal.Gui Kitty Keyboard

文件：`OneCode.App/Tui/TuiHost.cs:121-149`

当前使用：

- `Assembly.GetType`
- `Activator.CreateInstance`
- `Enum.Parse(Type, string)`
- `GetMethod`
- `MethodInfo.Invoke`

建议顺序：

1. 首选 Terminal.Gui 当前版本公开、强类型 API。
2. 若只是旧版本兼容 hack，评估删除，依赖 Terminal.Gui 自身协商。
3. 若功能不可缺少，封装为可选平台适配器并精确标注必要成员，同时增加 Linux 终端实机/PTY 测试。
4. 不允许保留整个 Terminal.Gui 程序集来解决这一小段兼容逻辑。

### 4.2 资源加载

以下嵌入资源调用本身可用于 AOT，但必须保证资源名静态可达：

- `Services/Agent/AgentTemplateConfig.cs:28`
- `Services/Coordinator/TeamOrchestrationService.cs:140`

要求：

- 使用常量或编译期生成资源清单；
- 测试 AOT 产物可加载内置 prompts 和 Team YAML；
- 同时验证外部覆盖文件仍可加载。

### 4.3 动态泛型和动态代码

全仓检查并禁止生产代码中的：

- `MakeGenericType` / `MakeGenericMethod`
- `System.Reflection.Emit`
- `Expression.Compile()`（除非使用 interpreter 且确认功能）
- `Assembly.Load*`
- `Activator.CreateInstance(Type, ...)`
- `Type.GetType(string)`
- C# `dynamic`

测试代码可以保留必要反射，但测试辅助程序集不进入发布闭包。

---

## Phase 5：逐项目和第三方依赖审计

### 目标

区分“一方代码问题”和“第三方包不支持 AOT”，形成明确的依赖处置矩阵。

### 5.1 项目启用顺序

1. `OneCode.Core`
2. `OneCode.Infrastructure`
3. `OneCode.Automation`
4. `OneCode.App`
5. `OneCode.Cli`

每个项目只有在分析器 0 warning 且测试通过后，才设置：

```xml
<IsAotCompatible>true</IsAotCompatible>
```

### 5.2 第三方依赖风险矩阵

| 依赖类别 | 代表包 | 审计重点 | 处置优先级 |
|---|---|---|---|
| Agent/AI | Microsoft.Agents.AI、Workflows、Mcp、Hyperlight | function schema、反射、动态代理、序列化 | P0 |
| TUI | Terminal.Gui、Terminal.Gui.Editor | driver 发现、反射、平台 native 调用 | P0 |
| 浏览器 | Microsoft.Playwright | Node driver 外部资源、进程启动、打包布局 | P0 |
| 协议 | ModelContextProtocol | JSON source generation、传输实现 | P0 |
| 配置 | YamlDotNet | 反射式 serializer/deserializer | P1 |
| LLM SDK | Anthropic、OllamaSharp、Extensions.AI.OpenAI | DTO 序列化、polymorphism、HTTP pipeline | P1 |
| Native | SkiaSharp、Hyperlight native assets | RID native library、静态/动态加载 | P1 |
| Shell/SSH | CliWrap、SSH.NET | 主要是运行时行为和平台支持 | P2 |

每个包需要记录：

- 精确版本；
- 是否声明 `IsAotCompatible`/`Trimmable`；
- AOT publish 告警；
- 官方支持声明或 issue；
- 关键功能冒烟结果；
- 最终决策：保留、升级、替换、隔离为非 AOT 功能。

### 5.3 Playwright 特殊说明

Native AOT 只会将 OneCode 托管代码编译为本机代码，不会把 Playwright 的 Node driver 和浏览器变成一个 exe。正式产物仍可能包含：

- `.playwright/package`
- `playwright.ps1`
- 浏览器或运行时资源

因此“AOT”与“真正零外部资源单文件”是两个不同目标，README 和发布文案必须分开描述。

### 5.4 功能降级策略

如果某个第三方组件无法 AOT：

1. 首先查找官方 AOT 兼容版本或配置。
2. 再考虑替换依赖。
3. 如果属于非核心功能，可通过编译条件形成 `OneCode.Cli.Aot` 功能子集，但必须在产品层明确差异。
4. 不建议维护两个长期分叉的业务代码树；功能差异应集中在基础设施适配层。

---

## Phase 6：AOT 发布产物与运行时验证

### 6.1 最小冒烟矩阵

| 领域 | 测试场景 | 验收 |
|---|---|---|
| 入口 | `--version`、`--help` | 退出码 0，内容正确 |
| TUI | 无配置启动、退出 | 无反射/资源异常 |
| 配置 | 创建、读取、更新配置 | round-trip 正确 |
| Session | 写入、列表、恢复、导出 | 多态消息无丢失 |
| 工具 | Read/Glob/Grep/AskUserQuestion 等代表工具 | Schema 创建和执行成功 |
| 权限 | 路径参数识别、未知参数类型 | 不发生权限降级 |
| MCP | stdio/WebSocket/registry DTO | 连接与 JSON 协议正常 |
| Build/Task/Team/Goal | 创建、持久化、恢复 | 状态机与证据完整 |
| Cron | 创建、列表、暂停、恢复、删除 | 持久化兼容 |
| LSP | 启动服务、请求、关闭 | JSON-RPC 正常 |
| WebFetch | HTTP 获取与 HTML 转 Markdown | AngleSharp 路径正常 |
| Playwright | driver 启动和最小页面操作 | 外部资源定位正确 |
| Prompts | 内置 prompt、Team YAML | 嵌入和外部覆盖均可加载 |

### 6.2 AOT 专用测试方式

普通单元测试运行在 CoreCLR 上，不能证明 Native AOT 产物可用。必须增加进程级测试：

1. CI 发布 AOT 可执行文件。
2. 测试脚本启动真实产物。
3. 使用临时 HOME/配置目录隔离用户环境。
4. 执行命令并检查退出码、stdout/stderr、生成文件。
5. 对持久化文件进行结构和 round-trip 校验。
6. 对 TUI 使用 PTY/ConPTY 驱动最小交互。

### 6.3 性能与体积基线

AOT 不是只为“能编译”。至少记录：

- 冷启动时间；
- 首次显示 TUI 时间；
- `--version` 峰值工作集；
- 空闲 TUI 内存；
- 主可执行文件大小；
- 完整发布目录大小；
- AOT 编译耗时；
- 与 ReadyToRun 当前产物的对比。

验收阈值不应预先拍脑袋设定；先采集 ReadyToRun 和 AOT 基线，再决定是否值得默认启用 AOT。

---

## Phase 7：CI/CD 集成

### 7.1 第一阶段：非阻断实验 Job

在 PR CI 中增加 `win-x64` 或 `linux-x64` AOT job：

- 构建失败时上传完整日志；
- 不影响普通 Release；
- 用于持续观测剩余告警数量；
- 输出告警按项目和错误码聚合。

此阶段只适用于重构进行中。

### 7.2 第二阶段：强制门禁

当仓库一方代码清零后：

- AOT publish 失败则 PR 失败；
- 任何新增 IL/AOT warning 失败；
- 运行最小 AOT smoke suite；
- 不允许新增 suppress，除非变更中同时包含原因、边界和测试。

### 7.3 第三阶段：正式 Release

建议平台推进顺序：

1. `win-x64`
2. `linux-x64`
3. `osx-arm64`
4. `win-arm64`
5. `linux-arm64`
6. `osx-x64`

每个 RID 必须在原生 runner 编译。Native AOT 不应依赖未经验证的跨平台交叉编译。

正式启用时：

- ReadyToRun 资产与 AOT 资产需要不同命名或清晰标识；
- 安装器需要明确选择渠道；
- checksum、压缩、安装模拟继续保留；
- 发布失败不得回退上传半成品。

---

## 7. 文件级改造清单

### P0：第一批必须修改

| 文件 | 改造 |
|---|---|
| `src/OneCode.Core/Tools/ToolServiceCollectionExtensions.cs` | 补 DI 标注；随后迁移强类型函数工厂 |
| `src/OneCode.Core/Tools/ToolRegistration.cs` | 删除 Type/MethodName 反射模型 |
| `src/OneCode.Core/Tools/ToolResult.cs` | 删除 `JsonSuccess(object)` |
| `src/OneCode.Core/Tools/ToolResultSerializer.cs` | 使用 CoreJsonContext |
| `src/OneCode.Core/Tools/ToolArgumentExtractor.cs` | 删除未知 object 序列化 fallback |
| `src/OneCode.Core/Keybindings/KeybindingSchema.cs` | 使用 JSON DOM 或源生成 DTO |
| `src/OneCode.App/Tools/ToolCatalog.cs` | 删除 `GetMethod` 和 MethodInfo 创建路径 |
| `src/OneCode.App/ServiceCollectionExtensions.Tools.cs` | 约 30+ 工具改为强类型工厂 |
| `src/OneCode.App/Session/SessionStore.cs` | 显式多态 envelope + Context |
| `src/OneCode.App/Tui/TuiHost.cs` | 删除或隔离 Kitty 反射 hack |

### P1：主要持久化和协议边界

- `Infrastructure/Build/JsonBuildRunStore.cs`
- `Infrastructure/Goals/DiskGoalCheckpointStore.cs`
- `Infrastructure/Tasks/JsonTaskStore.cs`
- `Infrastructure/Teams/JsonTeamRunStore.cs`
- `Infrastructure/Mcp/*`
- `Infrastructure/Config/ConfigManager.cs`
- `Infrastructure/Keybindings/KeybindingLoader.cs`
- `Infrastructure/Permissions/Yolo/YoloRuleFileStore.cs`
- `Infrastructure/Ai/VcrChatClientDecorator.cs`
- `Automation/Cron/*`
- `App/Services/PlanMode/*`
- `App/Services/GoalMode/*`
- `App/Services/Lsp/*`

### P2：构建、文档和 CI

- `src/Directory.Build.props` / 新增 `Directory.Build.targets`
- `src/OneCode.Cli/OneCode.Cli.csproj`
- `src/AGENTS.md`
- `src/OneCode.Cli/AGENTS.md`
- `scripts/build.ps1`
- `.github/workflows/release.yml`
- 新增 AOT smoke test 脚本和测试项目
- `README_CN.md`

---

## 8. 测试策略

### 8.1 单元测试

必须新增或强化：

- 每个 JsonContext 注册类型的 round-trip；
- 多态消息 envelope；
- 未知 discriminator；
- `ToolArgumentExtractor` 支持类型与拒绝类型；
- 工具注册名称和工厂完整性；
- Keybinding schema/template 快照；
- Telemetry converter 的所有允许值类型；
- 旧持久化格式兼容。

### 8.2 集成测试

- 使用 `JsonSerializerIsReflectionEnabledByDefault=false` 运行关键测试。
- DI 容器完整构建并实例化所有工具。
- 所有 AIFunction schema 在无反射 fallback 条件下生成。
- MCP、Session、Build、Task、Team、Goal、Cron 文件真实读写。

### 8.3 产物测试

- 必须测试 AOT 可执行文件，而不是仅测试项目 DLL。
- 测试日志必须保留 stdout/stderr 和退出码。
- 产物测试失败时上传临时配置、结构化日志和最小复现命令；不得上传密钥。

---

## 9. 告警与 suppress 治理

### 9.1 允许 suppress 的条件

只有同时满足以下条件才允许：

1. API 的所有可能运行时类型/成员已静态枚举；
2. 有精确的 `DynamicallyAccessedMembers`、`DynamicDependency` 或最小 descriptor；
3. 注释说明为什么安全；
4. 有 AOT 产物测试覆盖该路径；
5. suppress 作用域限制到最小方法或成员。

### 9.2 禁止做法

- `<NoWarn>IL2026;IL3050;...</NoWarn>` 全局压制。
- 对整个程序集添加 `UnconditionalSuppressMessage`。
- 通过 `TrimmerRootAssembly` 保留所有项目程序集。
- 开启 `JsonSerializerIsReflectionEnabledByDefault=true` 规避改造。
- 把失败的功能静默跳过但仍声称完整 AOT 支持。

### 9.3 代码审查要求

任何引入以下内容的 PR 必须标记 AOT review：

- 反射 API；
- 新 JSON DTO 或 converter；
- 新 NuGet 依赖；
- 动态代理、表达式编译、运行时泛型构造；
- native library；
- 新的嵌入资源或运行时加载资源；
- AOT/linker suppress。

---

## 10. 风险与回滚策略

| 风险 | 影响 | 缓解 |
|---|---|---|
| MAF/Terminal.Gui 等第三方依赖不支持 AOT | 核心功能无法进入 AOT | 优先验证官方支持；必要时升级/替换/隔离功能 |
| JSON 文件格式变化 | 用户历史 Session/任务丢失 | envelope 版本化、兼容读取、迁移测试 |
| 工具注册大规模修改 | 工具缺失或 schema 变化 | 注册完整性测试、工具清单快照、逐模块迁移 |
| AOT 产物仍需 Playwright/native 资源 | “单文件”预期落差 | 明确发布语义，验证完整目录而非只看 exe |
| Preview SDK 行为变化 | 构建不稳定 | 固定 SDK，升级通过独立 PR |
| AOT 编译时间和 CI 成本增加 | PR 反馈变慢 | 先单 RID 门禁，正式 Release 再跑全矩阵 |
| 性能收益不足 | 改造成本无法回收 | 与 ReadyToRun 建立量化对比，保留双渠道决策 |

回滚原则：

- 每个 Phase 独立提交，保持普通 Release 始终可用。
- 不在同一提交同时改 JSON 文件格式和删除旧格式读取。
- AOT Release 初期与 ReadyToRun 并行，至少经过一个完整版本周期再考虑切换默认渠道。

---

## 11. 完成定义（Definition of Done）

### 11.1 代码

- [ ] 一方生产代码无未审计的反射实例化、字符串方法查找和动态代码生成。
- [ ] 一方 JSON 路径全部使用 `JsonTypeInfo`、`JsonSerializerContext`、JSON DOM 或 `Utf8JsonWriter`。
- [ ] `JsonSerializerIsReflectionEnabledByDefault=false`。
- [ ] 所有项目按依赖顺序声明并验证 `IsAotCompatible=true`。
- [ ] 不存在全局 IL/AOT suppress。

### 11.2 构建

- [ ] Release Build 0 warning、0 error。
- [ ] 完整单元/集成测试全绿。
- [ ] `win-x64` AOT publish 0 warning、0 error。
- [ ] `linux-x64` AOT publish 0 warning、0 error。
- [ ] 其余目标 RID 在各自原生 runner 通过。

### 11.3 运行时

- [ ] AOT smoke matrix 全绿。
- [ ] Session/Build/Task/Team/Goal/Cron 旧数据兼容。
- [ ] TUI 和终端键盘协议无功能回退。
- [ ] MCP、LSP、Playwright、WebFetch 可用。
- [ ] prompts、Team YAML、native runtime 资源可定位。

### 11.4 发布

- [ ] CI 将 AOT 构建和冒烟测试设为强制门禁。
- [ ] AOT 资产命名、checksum、安装、升级链路通过。
- [ ] README 明确 AOT 与单文件/外部资源的区别。
- [ ] `src/AGENTS.md`、CLI 约束、csproj、build script、Release workflow 完全一致。

---

## 12. 建议的提交拆分

建议按以下顺序提交，避免一次性大爆炸：

1. `build(aot): pin SDK and add reproducible AOT audit target`
2. `refactor(core): remove reflection-based JSON serialization`
3. `refactor(tools): replace method-name registration with typed factories`
4. `refactor(session): add source-generated polymorphic persistence`
5. `refactor(infrastructure): migrate persistence stores to JsonContext`
6. `refactor(automation): migrate cron serialization to JsonContext`
7. `refactor(app): migrate protocol and tool JSON paths`
8. `refactor(tui): remove reflective Terminal.Gui compatibility path`
9. `test(aot): add published-binary smoke suite`
10. `ci(aot): enforce win-x64 and linux-x64 AOT gates`
11. `release(aot): publish opt-in AOT assets`
12. `docs(aot): mark Native AOT as supported after full verification`

每个提交必须满足：普通 Release 不回退、测试全绿、AOT 告警数量不增加。

---

## 13. 第一轮实施建议

第一轮不要立刻改全仓。建议只完成以下闭环：

1. 固定 SDK 和依赖版本。
2. 修正文档配置冲突。
3. 新建 `CoreJsonContext`。
4. 修复当前 11 个 Core 错误。
5. 删除 `ToolResult.JsonSuccess(object)` 和 `ToolArgumentExtractor` 任意对象 fallback。
6. 为 Core 增加 AOT Analyzer 门禁和测试。
7. 再次运行完整 AOT publish，生成第二轮阻塞清单。

第一轮验收结果应是：

- `OneCode.Core` 达到 `IsAotCompatible=true`；
- 当前 11 个错误归零；
- AOT 构建继续推进到下一个项目；
- 普通 Release 和 1769 项既有测试不回退；
- 得到真实的 Infrastructure/App/第三方依赖告警基线。

这比一次性修改所有 JSON 调用更可控，也能尽早判断 MAF、Terminal.Gui、Playwright 是否构成不可绕过的第三方阻塞。
