namespace IAMS.Web.Services;

public class SnackbarService
{
    private readonly List<SnackbarMessage> _messages = new();
    private readonly object _lock = new();

    public IReadOnlyList<SnackbarMessage> Messages
    {
        get
        {
            lock (_lock)
            {
                return _messages.ToList();
            }
        }
    }

    public event Action? OnChange;

    public void Show(string message, SnackbarType type = SnackbarType.Info, int durationMs = 4000)
    {
        var snackbar = new SnackbarMessage
        {
            Id = Guid.NewGuid(),
            Message = message,
            Type = type,
            CreatedAt = DateTime.UtcNow,
            DurationMs = durationMs
        };

        lock (_lock)
        {
            _messages.Add(snackbar);
        }

        OnChange?.Invoke();

        if (durationMs > 0)
        {
            _ = Task.Delay(durationMs).ContinueWith(_ => Remove(snackbar.Id));
        }
    }

    public void Success(string message, int durationMs = 4000)
        => Show(message, SnackbarType.Success, durationMs);

    public void Error(string message, int durationMs = 6000)
        => Show(message, SnackbarType.Error, durationMs);

    public void Warning(string message, int durationMs = 5000)
        => Show(message, SnackbarType.Warning, durationMs);

    public void Info(string message, int durationMs = 4000)
        => Show(message, SnackbarType.Info, durationMs);

    public void Remove(Guid id)
    {
        lock (_lock)
        {
            _messages.RemoveAll(m => m.Id == id);
        }
        OnChange?.Invoke();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _messages.Clear();
        }
        OnChange?.Invoke();
    }
}

public class SnackbarMessage
{
    public Guid Id { get; init; }
    public required string Message { get; init; }
    public SnackbarType Type { get; init; }
    public DateTime CreatedAt { get; init; }
    public int DurationMs { get; init; }
}

public enum SnackbarType
{
    Info,
    Success,
    Warning,
    Error
}
