using System.ComponentModel.DataAnnotations.Schema;

namespace AlertingSystem.Backend.Models;

public sealed class CheckHistoryEntry
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Endpoint { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public long? ResponseTimeMs { get; set; }

    [NotMapped] public string TimestampDisplay => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
    [NotMapped] public string ResponseTimeDisplay => ResponseTimeMs is long ms ? $"{ms} ms" : "—";
    [NotMapped] public string DisplayText =>
        $"{Timestamp:HH:mm:ss} - {Endpoint} - {Result}" +
        (ResponseTimeMs is long ms ? $" - {ms} ms" : string.Empty);
}
