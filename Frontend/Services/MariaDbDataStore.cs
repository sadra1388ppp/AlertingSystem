using AlertingSystem.Contracts;
using AlertingSystem.Models;

namespace AlertingSystem.Services;

public sealed class MariaDbDataStore
{
    public List<Incident> LoadIncidents() =>
        BackendApiClient.Get<List<IncidentDto>>("api/incidents").Select(FromDto).ToList();

    public List<CheckHistoryEntry> LoadCheckHistory() =>
        BackendApiClient.Get<List<CheckHistoryEntryDto>>("api/history").Select(FromDto).ToList();

    public void SaveIncidents(IEnumerable<Incident> incidents)
    {
        var result = BackendApiClient.Put<OperationResponse>(
            "api/incidents", incidents.Select(ToDto).ToList());

        if (!result.Success)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.Error) ? "Incidents were not saved." : result.Error);
    }

    public bool SaveIncident(Incident incident, bool allowInsert = false)
    {
        var result = BackendApiClient.Put<OperationResponse>(
            "api/incidents/item",
            new IncidentSaveRequest { Incident = ToDto(incident), AllowInsert = allowInsert });
        return result.Success;
    }

    public void SaveCheckHistory(IEnumerable<CheckHistoryEntry> history)
    {
        foreach (var item in history)
            SaveCheckHistoryEntry(item);
    }

    public void SaveCheckHistoryEntry(CheckHistoryEntry entry)
    {
        var result = BackendApiClient.Put<OperationResponse>("api/history/item", ToDto(entry));

        if (!result.Success)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.Error) ? "The history entry was not saved." : result.Error);

        if (result.Id is long id)
            entry.Id = id;
    }

    private static IncidentDto ToDto(Incident item) => new()
    {
        IncidentNumber = item.IncidentNumber,
        EndpointUrl = item.EndpointUrl,
        Title = item.Title,
        Details = item.Details,
        ResolutionNote = item.ResolutionNote,
        Reason = item.Reason,
        Severity = (int)item.Severity,
        Status = (int)item.Status,
        FirstOccurredAt = item.FirstOccurredAt,
        LastOccurredAt = item.LastOccurredAt,
        ResolvedAt = item.ResolvedAt,
        OccurrenceCount = item.OccurrenceCount
    };

    private static Incident FromDto(IncidentDto item) => new()
    {
        IncidentNumber = item.IncidentNumber,
        EndpointUrl = item.EndpointUrl,
        Title = item.Title,
        Details = item.Details,
        ResolutionNote = item.ResolutionNote,
        Reason = item.Reason,
        Severity = Enum.IsDefined(typeof(IncidentSeverity), item.Severity)
            ? (IncidentSeverity)item.Severity : IncidentSeverity.High,
        Status = Enum.IsDefined(typeof(IncidentStatus), item.Status)
            ? (IncidentStatus)item.Status : IncidentStatus.Open,
        FirstOccurredAt = item.FirstOccurredAt,
        LastOccurredAt = item.LastOccurredAt,
        ResolvedAt = item.ResolvedAt,
        OccurrenceCount = Math.Max(1, item.OccurrenceCount)
    };

    private static CheckHistoryEntryDto ToDto(CheckHistoryEntry item) => new()
    {
        Id = item.Id,
        Timestamp = item.Timestamp,
        Endpoint = item.Endpoint,
        Result = item.Result,
        Details = item.Details,
        ResponseTimeMs = item.ResponseTimeMs
    };

    private static CheckHistoryEntry FromDto(CheckHistoryEntryDto item) => new()
    {
        Id = item.Id,
        Timestamp = item.Timestamp,
        Endpoint = item.Endpoint,
        Result = item.Result,
        Details = item.Details,
        ResponseTimeMs = item.ResponseTimeMs
    };
}
