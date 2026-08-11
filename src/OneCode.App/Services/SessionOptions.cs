namespace OneCode.App.Services;

/// <summary>Session-scoped startup options registered at the composition root.</summary>
public sealed class SessionOptions
{
    public string InitialWorkingDirectory { get; set; } = Environment.CurrentDirectory;
}
