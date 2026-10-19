using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace AlertingSystem.Models;

public enum IncidentSeverity
{
    Critical = 0,
    High = 1,
    Medium = 2,
    Low = 3
}

public enum IncidentStatus
{
    Open = 0,
    Acknowledged = 1,
    Resolved = 2
}

public sealed class Incident : INotifyPropertyChanged
{
    private string _incidentNumber = string.Empty;
    private string _endpointUrl = string.Empty;
    private string _title = string.Empty;
    private string _details = string.Empty;
    private string _resolutionNote = string.Empty;
    private IncidentSeverity _severity = IncidentSeverity.High;
    private IncidentStatus _status = IncidentStatus.Open;
    private DateTime _firstOccurredAt = DateTime.Now;
    private DateTime _lastOccurredAt = DateTime.Now;
    private DateTime? _acknowledgedAt;
    private DateTime? _resolvedAt;
    private int _occurrenceCount = 1;

    public string IncidentNumber
    {
        get => _incidentNumber;
        set => SetField(ref _incidentNumber, value);
    }

    public string EndpointUrl
    {
        get => _endpointUrl;
        set => SetField(ref _endpointUrl, value);
    }

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string Details
    {
        get => _details;
        set => SetField(ref _details, value);
    }

    public string ResolutionNote
    {
        get => _resolutionNote;
        set => SetField(ref _resolutionNote, value);
    }

    public IncidentSeverity Severity
    {
        get => _severity;
        set
        {
            if (SetField(ref _severity, value))
            {
                OnPropertyChanged(nameof(SeverityDisplay));
                OnPropertyChanged(nameof(SeverityBrush));
            }
        }
    }

    public IncidentStatus Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusDisplay));
                OnPropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public DateTime FirstOccurredAt
    {
        get => _firstOccurredAt;
        set
        {
            if (SetField(ref _firstOccurredAt, value))
            {
                OnPropertyChanged(nameof(ActivityDisplay));
            }
        }
    }

    public DateTime LastOccurredAt
    {
        get => _lastOccurredAt;
        set
        {
            if (SetField(ref _lastOccurredAt, value))
            {
                OnPropertyChanged(nameof(ActivityDisplay));
            }
        }
    }

    public DateTime? AcknowledgedAt
    {
        get => _acknowledgedAt;
        set => SetField(ref _acknowledgedAt, value);
    }

    public DateTime? ResolvedAt
    {
        get => _resolvedAt;
        set => SetField(ref _resolvedAt, value);
    }

    public int OccurrenceCount
    {
        get => _occurrenceCount;
        set
        {
            if (SetField(ref _occurrenceCount, value))
            {
                OnPropertyChanged(nameof(ActivityDisplay));
            }
        }
    }

    [JsonIgnore]
    public string SeverityDisplay => Severity.ToString();

    [JsonIgnore]
    public string StatusDisplay => Status switch
    {
        IncidentStatus.Acknowledged => "Seen",
        _ => Status.ToString()
    };

    [JsonIgnore]
    public Brush SeverityBrush => Severity switch
    {
        IncidentSeverity.Critical => Brushes.Crimson,
        IncidentSeverity.High => Brushes.OrangeRed,
        IncidentSeverity.Medium => Brushes.Goldenrod,
        _ => Brushes.SteelBlue
    };

    [JsonIgnore]
    public Brush StatusBrush => Status switch
    {
        IncidentStatus.Resolved => Brushes.MediumAquamarine,
        IncidentStatus.Acknowledged => Brushes.Gold,
        _ => Brushes.LightCoral
    };

    [JsonIgnore]
    public string ActivityDisplay =>
        $"Started {FirstOccurredAt:yyyy-MM-dd HH:mm:ss}  •  Last failure {LastOccurredAt:yyyy-MM-dd HH:mm:ss}  •  {OccurrenceCount} occurrence(s)";

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
