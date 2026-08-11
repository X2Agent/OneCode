namespace OneCode.App.Tui;

/// <summary>
/// Application entry point for the Terminal.Gui interactive REPL.
///
/// Callers (i.e., <c>OneCodeApp.ExecuteInteractiveAsync</c>) build a
/// <see cref="TuiContext"/> with the pre-resolved services, then call
/// <see cref="Run(Func{IApplication, OneCodeToplevel})"/> which blocks until the user quits.
/// </summary>
public static class TuiHost
{
    /// <summary>
    /// Indicates whether the kitty keyboard protocol was successfully detected.
    /// Set during <see cref="Run"/>; read by TUI components to show a status indicator.
    /// </summary>
    public static bool KittyKeyboardSupported { get; private set; }

    /// <summary>
    /// Initialise Terminal.Gui, run the full-screen REPL, then shut down.
    /// Returns the process exit code (0 = clean exit, non-zero = error).
    ///
    /// Must be called from the OS thread that owns the console window
    /// (i.e. the process main thread on Windows).
    /// </summary>
    public static int Run(Func<IApplication, OneCodeToplevel> toplevelFactory)
    {
        Console.CancelKeyPress += (_, e) => e.Cancel = true;

        // Pre-enable kitty keyboard protocol BEFORE Terminal.Gui initializes.
        // This ensures the terminal is already in kitty mode when Terminal.Gui's
        // auto-detection (CSI ?u query) runs during app.Init(), so the detection
        // succeeds and Shift+Enter is decoded as Key.Enter.WithShift.
        EnableKittyKeyboard();

        try
        {
            using var app = Application.Create();
            app.Init();

            // Diagnose kitty keyboard protocol support — Terminal.Gui auto-detects
            // and enables it when the terminal advertises support. With our pre-enable,
            // detection should succeed on any kitty-capable terminal.
            var caps = app.Driver?.KittyKeyboardCapabilities;
            if (caps is { IsSupported: true })
            {
                KittyKeyboardSupported = true;
                Console.Error.WriteLine(
                    $"[kitty] keyboard protocol supported (flags={caps.Flags}); Shift+Enter should work.");
            }
            else
            {
                KittyKeyboardSupported = false;
                Console.Error.WriteLine(
                    "[kitty] keyboard protocol NOT detected after pre-enable; attempting force-enable...");
                ForceEnableKittyKeyboard(app.Driver);
            }

            var toplevel = toplevelFactory(app);

            try
            {
                toplevel.ScheduleInitialFocus();
                app.Run(toplevel);
                return toplevel.ExitCode;
            }
            finally
            {
                toplevel.Dispose();
            }
            // app is disposed here (using var) — terminal restored to normal mode.
        }
        finally
        {
            // After Terminal.Gui has shut down and the terminal is back in cooked mode,
            // restore the kitty keyboard protocol state. Sending ESC[<u while Terminal.Gui
            // is still in alternate-screen mode would be swallowed, so this must run
            // AFTER app.Dispose().
            DisableKittyKeyboard();
        }
    }

    // Kitty keyboard protocol helpers
    //
    // The kitty keyboard protocol encodes keys unambiguously as CSI u sequences.
    // Without it, Shift+Enter is indistinguishable from plain Enter (both send \r),
    // making the Shift+Enter → newline binding in ChatTextEditor.cs ineffective.
    //
    // Terminal.Gui auto-detects kitty support during app.Init() by sending CSI ?u.
    // However, detection can fail under ConPTY or when the terminal doesn't respond
    // in time. We use a three-layer strategy:
    //   1. Pre-enable (ESC[>1u) before app.Init() — so detection sees an active terminal
    //   2. Force-enable via KittyKeyboardProtocolDetector — updates Terminal.Gui's internal state
    //   3. Restore (ESC[<u) on shutdown — clean terminal state

    /// <summary>
    /// Sends ESC[>1u to push the current kitty keyboard flags and enable
    /// DisambiguateEscapeCodes (flag 1). This makes the terminal encode
    /// Shift+Enter as ESC[13;2u instead of a bare \r.
    /// </summary>
    private static void EnableKittyKeyboard()
    {
        try
        {
            Console.Out.Write("\x1b[>1u");
            Console.Out.Flush();
        }
        catch (Exception ex)
        {
            // Sending escape sequences may fail in non-terminal environments.
            // 纯静态方法无法注入 ILogger，按 §5.1 兑底使用 Debug.WriteLine。
            System.Diagnostics.Debug.WriteLine($"TuiHost.EnableKittyKeyboard failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Force-enable kitty keyboard protocol via Terminal.Gui's detector API.
    /// This both sends the enable sequence AND updates the driver's internal
    /// capabilities state, so Terminal.Gui knows to decode CSI u key sequences
    /// even if auto-detection failed.
    /// </summary>
    private static void ForceEnableKittyKeyboard(object? driver)
    {
        if (driver is null) return;
        try
        {
            // Use reflection to avoid a hard dependency on Terminal.Gui.Drivers namespace
            // (types may be in a different namespace depending on TG version).
            var driverType = driver.GetType();
            var detectorType = driverType.Assembly.GetType("Terminal.Gui.Drivers.KittyKeyboardProtocolDetector");
            if (detectorType is null)
            {
                Console.Error.WriteLine("[kitty] KittyKeyboardProtocolDetector type not found; relying on pre-enable only.");
                return;
            }

            var detector = Activator.CreateInstance(detectorType, driver, null);
            if (detector is null) return;

            var flagsType = driverType.Assembly.GetType("Terminal.Gui.Drivers.KittyKeyboardFlags");
            if (flagsType is null) return;
            var flagValue = Enum.Parse(flagsType, "DisambiguateEscapeCodes");

            var enableMethod = detectorType.GetMethod("Enable");
            if (enableMethod is null) return;
            enableMethod.Invoke(detector, new[] { flagValue });

            Console.Error.WriteLine("[kitty] force-enable via detector API completed.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[kitty] detector API failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends ESC[&lt;u to pop/restore the previously pushed kitty keyboard flag state.
    /// Must be called after Terminal.Gui has shut down (terminal in cooked mode).
    /// </summary>
    private static void DisableKittyKeyboard()
    {
        try
        {
            Console.Out.Write("\x1b[<u");
            Console.Out.Flush();
        }
        catch (Exception ex)
        {
            // Safe to ignore during shutdown.
            // 纯静态方法无法注入 ILogger，按 §5.1 兑底使用 Debug.WriteLine。
            System.Diagnostics.Debug.WriteLine($"TuiHost.DisableKittyKeyboard failed: {ex.Message}");
        }
    }
}
