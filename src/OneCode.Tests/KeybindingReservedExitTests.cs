using NSubstitute;
using OneCode.Core.Keybindings;

namespace OneCode.Tests;

/// <summary>
/// 保留键回归锚定：ctrl+d 在 Chat 上下文恒活跃的前提下必须仍解析为 app:exit。
/// Chat 绑定块声明在 Global 之后、Resolver「后匹配生效」——一旦 Chat 重新绑定
/// ctrl+d（如曾经引入的 chat:pageDown），保留退出键会被永久遮蔽成死键。
/// </summary>
public sealed class KeybindingReservedExitTests
{
    [Fact]
    public void Resolve_CtrlD_WithChatContextActive_ResolvesAppExit()
    {
        var resolver = new KeybindingResolver();
        resolver.SetBindings([.. KeybindingDefaults.GetDefaultParsedBindings()]);

        // 模拟真实运行态：InteractiveKeybindingService 启动即永久 Push ContextChat
        var active = new KeybindingContextManager { FocusContext = KeybindingDefaults.ContextChat }.ActiveContexts;

        var key = Substitute.For<IKeyInput>();
        key.Input.Returns("d");
        key.Ctrl.Returns(true);

        var result = resolver.Resolve(key, active);

        result.Result.Should().Be(KeyResolveResult.Match);
        result.Action.Should().Be(KeybindingDefaults.ActionAppExit);
    }
}