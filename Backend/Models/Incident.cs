using System.ComponentModel.DataAnnotations.Schema;

namespace AlertingSystem.Backend.Models;

public enum IncidentSeverity { Critical = 0, High = 1, Medium = 2, Low = 3 }
public enum IncidentStatus { Open = 0, Done = 2 }

public sealed class Incident
{
    public string IncidentNumber { get; set; } = string.Empty;
    public string EndpointUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string ResolutionNote { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public IncidentSeverity Severity { get; set; } = IncidentSeverity.High;
    public IncidentStatus Status { get; set; } = IncidentStatus.Open;
    public DateTime FirstOccurredAt { get; set; } = DateTime.Now;
    public DateTime LastOccurredAt { get; set; } = DateTime.Now;
    public DateTime? ResolvedAt { get; set; }
    public int OccurrenceCount { get; set; } = 1;

    [NotMapped] public string SeverityDisplay => Severity.ToString();
    [NotMapped] public string StatusDisplay => Status == IncidentStatus.Done ? "Done" : "Open";
    [NotMapped] public string ActivityDisplay =>
        $"Started {FirstOccurredAt:yyyy-MM-dd HH:mm:ss} - Last failure {LastOccurredAt:yyyy-MM-dd HH:mm:ss} - {OccurrenceCount} occurrence(s)";
}
