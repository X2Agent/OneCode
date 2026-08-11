using OneCode.App.Services.Streaming;
using OneCode.App.Tui;
using OneCode.Core.Models;
using IAppStateAccessor = OneCode.Core.Domain.IAppStateAccessor;

namespace OneCode.App.Services;

/// <summary>Streaming pipeline services for TUI context construction.</summary>
public sealed record TuiStreamingDependencies(
    QueryStreamService QueryStream,
    SlashCommandPipeline SlashCommands,
    InputQueue InputQueue);

/// <summary>Catalog / registry surfaces wired into <see cref="TuiContext"/>.</summary>
public sealed record TuiCatalogDependencies(
    IModelManager ModelManager,
    IModelCatalog ModelCatalog,
    IAppStateAccessor AppState,
    ICommandRegistry CommandRegistry,
    IToolCatalog ToolCatalog);
