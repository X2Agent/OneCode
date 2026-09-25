using System.Runtime.CompilerServices;

namespace OneCode.Tests.TestSupport.Tui;

/// <summary>
/// headless TUI 测试的进程级前置条件。
///
/// 与 Terminal.Gui 自身测试项目同款做法：在任何测试代码之前设 <c>DisableRealDriverIO=1</c>，
/// 使 <c>Driver.IsAttachedToTerminal()</c> 恒为 false —— 否则真实驱动会去摸控制台句柄
/// （在 CI / 无 tty 环境下抛异常，在开发者机器上则会污染真实终端）。
/// </summary>
internal static class TuiTestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize() => Environment.SetEnvironmentVariable("DisableRealDriverIO", "1");
}
