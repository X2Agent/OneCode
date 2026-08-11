using System.Threading.Channels;

namespace OneCode.App.Session;

public enum SessionRunState
{
    Idle,
    Running,
    Completed,
}

/// <summary>
/// A conversation suspended from the foreground, optionally still executing a query.
/// </summary>
public sealed class BackgroundSession
{
    public BackgroundSession(Conversation conversation)
    {
        Conversation = conversation;
    }

    public Conversation Conversation { get; }

    public SessionRunState RunState { get; set; } = SessionRunState.Idle;

    public CancellationTokenSource? QueryCancellation { get; set; }

    public Task? RunningTask { get; set; }

    public Channel<object>? EventBuffer { get; set; }
}
