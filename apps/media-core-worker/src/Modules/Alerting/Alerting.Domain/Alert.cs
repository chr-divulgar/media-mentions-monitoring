namespace MediaOpsCore.Modules.Alerting.Domain;

public sealed class Alert
{
    public Alert(
        string text,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        string media,
        string platform,
        string filePath,
        IReadOnlyList<string> words,
        string clientName,
        AlertType type)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        StartTime = startTime;
        EndTime = endTime;
        Media = string.IsNullOrWhiteSpace(media) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(media)) : media;
        Platform = string.IsNullOrWhiteSpace(platform) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(platform)) : platform;
        FilePath = filePath ?? string.Empty;
        Words = words ?? Array.Empty<string>();
        ClientName = string.IsNullOrWhiteSpace(clientName) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(clientName)) : clientName;
        Type = type;
    }

    public string Text { get; }

    public DateTimeOffset StartTime { get; }

    public DateTimeOffset EndTime { get; }

    public string Media { get; }

    public string Platform { get; }

    public string FilePath { get; }

    public IReadOnlyList<string> Words { get; }

    public string ClientName { get; }

    public AlertType Type { get; }
}
