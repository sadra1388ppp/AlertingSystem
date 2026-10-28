using System.Text.Json.Serialization;
using System.Windows.Media;

namespace AlertingSystem.Models;

public sealed class CheckHistoryEntry
{
    public long Id { get; set; }
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
            if (Result.StartsWith("HTTP 2", StringComparison.OrdinalIgnoreCase) ||
                Result.Equals("LOGIN SUCCESS", StringComparison.OrdinalIgnoreCase) ||
                Result.Equals("ACCOUNT CREATED", StringComparison.OrdinalIgnoreCase))
            {
                return Brushes.MediumAquamarine;
            }

            if (Result.Equals("SIGN OUT", StringComparison.OrdinalIgnoreCase))
            {
                return Brushes.Goldenrod;
            }

            if (Result.Equals("ACCOUNT DELETED", StringComparison.OrdinalIgnoreCase) ||
                Result.Equals("SESSION REVOKED", StringComparison.OrdinalIgnoreCase) ||
                Result.Equals("LOGIN FAILED", StringComparison.OrdinalIgnoreCase) ||
                Result.Equals("LOGIN ERROR", StringComparison.OrdinalIgnoreCase))
            {
                return Brushes.LightCoral;
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
