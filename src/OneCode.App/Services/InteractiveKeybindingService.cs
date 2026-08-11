using OneCode.Core.Keybindings;

namespace OneCode.App.Services;

/// <summary>
/// Loads user keybindings (with hot-reload) or falls back to defaults.
/// </summary>
public sealed class InteractiveKeybindingService(
    InteractiveTuiDependencies tuiDeps,
    ILogger<InteractiveKeybindingService> logger)
{
    public async Task<(KeybindingResolver Resolver, KeybindingContextManager ContextManager)> InitializeAsync(
        CancellationToken ct)
    {
        var keyResolver = new KeybindingResolver();
        var keyContextManager = new KeybindingContextManager();

        try
        {
            if (tuiDeps.KeybindingLoader is { } loader)
            {
                var loadResult = await loader.LoadAsync(ct).ConfigureAwait(false);
                keyResolver.SetBindings([.. loadResult.Bindings]);

                foreach (var warning in loadResult.Warnings)
                    logger.LogWarning("Keybinding warning [{Severity}]: {Message}", warning.Severity, warning.Message);

                loader.BindingsChanged += result =>
                {
                    keyResolver.SetBindings([.. result.Bindings]);
                    logger.LogDebug("Keybindings reloaded ({Count} entries)", result.Bindings.Length);
                };
                loader.InitializeWatcher();
            }
            else
            {
                keyResolver.SetBindings([.. KeybindingDefaults.GetDefaultParsedBindings()]);
            }

            keyContextManager.PushContext(KeybindingDefaults.ContextChat);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to initialize keybinding system, using hardcoded defaults");
            keyResolver.SetBindings([.. KeybindingDefaults.GetDefaultParsedBindings()]);
            keyContextManager.PushContext(KeybindingDefaults.ContextChat);
        }

        return (keyResolver, keyContextManager);
    }
}
