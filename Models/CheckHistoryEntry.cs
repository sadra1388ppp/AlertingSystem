using System.Text.Json.Serialization;
using System.Windows.Media;

namespace AlertingSystem.Models;

public sealed class CheckHistoryEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Endpoint { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public long? ResponseTimeMs { get; set; }

    [JsonIgnore]
    public string TimestampDisplay => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");

    [JsonIgnore]
    public string ResponseTimeDisplay => ResponseTimeMs is long ms ? $"{ms} ms" : "—";

    [JsonIgnore]
    public Brush ResultBrush
    {
        get
        {
            if (Result.StartsWith("HTTP 2", StringComparison.OrdinalIgnoreCase))
            {
                return Brushes.MediumAquamarine;
            }

            if (Result.Equals("Invalid URL", StringComparison.OrdinalIgnoreCase))
            {
                return Brushes.Goldenrod;
            }

            return Brushes.LightCoral;
        }
    }

    [JsonIgnore]
    public string DisplayText =>
        $"{Timestamp:HH:mm:ss}  •  {Endpoint}  •  {Result}" +
        (ResponseTimeMs is long ms ? $"  •  {ms} ms" : string.Empty);
}
