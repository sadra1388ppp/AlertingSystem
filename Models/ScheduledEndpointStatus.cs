using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace AlertingSystem.Models;

/// <summary>
/// View-model for one endpoint in the scheduled-monitoring list.
/// Runtime results are read from the current session; the endpoint list itself is persisted separately.
/// </summary>
public sealed class ScheduledEndpointStatus : INotifyPropertyChanged
{
    private string _lastResult = "Not checked";
    private DateTime? _lastCheckedAt;
    private long? _responseTimeMs;

    public ScheduledEndpointStatus(string endpoint)
    {
        Endpoint = endpoint;
    }

    public string Endpoint { get; }

    public string LastResult
    {
        get => _lastResult;
        set
        {
            if (SetField(ref _lastResult, value))
            {
                OnPropertyChanged(nameof(ResultBrush));
            }
        }
    }

    public DateTime? LastCheckedAt
    {
        get => _lastCheckedAt;
        set
        {
            if (SetField(ref _lastCheckedAt, value))
            {
                OnPropertyChanged(nameof(LastCheckedDisplay));
            }
        }
    }

    public long? ResponseTimeMs
    {
        get => _responseTimeMs;
        set
        {
            if (SetField(ref _responseTimeMs, value))
            {
                OnPropertyChanged(nameof(ResponseTimeDisplay));
            }
        }
    }

    [JsonIgnore]
    public string LastCheckedDisplay => LastCheckedAt is DateTime checkedAt
        ? $"Last checked {checkedAt:yyyy-MM-dd HH:mm:ss}"
        : "Waiting for first check";

    [JsonIgnore]
    public string ResponseTimeDisplay => ResponseTimeMs is long elapsed
        ? $"{elapsed} ms"
        : "—";

    [JsonIgnore]
    public Brush ResultBrush => LastResult switch
    {
        "Not checked" => Brushes.SteelBlue,
        "Checking…" => Brushes.DeepSkyBlue,
        "Healthy" => Brushes.MediumAquamarine,
        "Invalid URL" => Brushes.Goldenrod,
        _ when LastResult.StartsWith("HTTP 2", StringComparison.OrdinalIgnoreCase) => Brushes.MediumAquamarine,
        _ => Brushes.LightCoral
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
