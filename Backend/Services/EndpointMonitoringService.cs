using System.Diagnostics;
using AlertingSystem.Backend.Models;

namespace AlertingSystem.Backend.Services;

public sealed class EndpointMonitoringService
{
    private readonly MariaDbDataStore _dataStore;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public EndpointMonitoringService(MariaDbDataStore dataStore)
    {
        _dataStore = dataStore;
    }

    public async Task<AlertingSystem.Contracts.MonitoringResult> CheckAsync(
        string address,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            const string details = "Enter a valid URL beginning with http:// or https://.";
            SaveHistory(string.IsNullOrWhiteSpace(address) ? "(empty URL)" : address,
                "Invalid URL", details, null);
            return new()
            {
                Status = "Invalid URL",
                BrushKey = "DangerBrush",
                Message = details,
                ResponseTime = "—",
                ResponseSummary = "—"
            };
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await _httpClient.GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            stopwatch.Stop();

            var statusCode = (int)response.StatusCode;
            var elapsed = stopwatch.ElapsedMilliseconds;

            if (response.IsSuccessStatusCode)
            {
                var resolvedIncident = MarkMatchingIncidentDone(uri,
                    $"Endpoint returned HTTP {statusCode}.");
                var details = resolvedIncident is null
                    ? $"Successful response ({response.ReasonPhrase})."
                    : $"Successful response; incident {resolvedIncident} was automatically marked Done.";

                var message = resolvedIncident is null
                    ? $"{uri.Host} returned HTTP {statusCode} ({response.ReasonPhrase})."
                    : $"{uri.Host} recovered. Incident {resolvedIncident} was marked Done.";

                SaveHistory(uri.AbsoluteUri, $"HTTP {statusCode}", details, elapsed);
                return new()
                {
                    Status = "Healthy",
                    BrushKey = "SuccessBrush",
                    Message = message,
                    ResponseTime = $"{elapsed} ms",
                    ResponseSummary = $"{elapsed} ms",
                    ResponseTimeMs = elapsed
                };
            }

            var failureDetails = $"Endpoint returned HTTP {statusCode} ({response.ReasonPhrase}).";
            var incident = UpsertIncident(
                uri, GetHttpFailureTitle(statusCode), GetHttpFailureSeverity(statusCode), failureDetails);
            var historyDetails = $"{failureDetails} Incident {incident.IncidentNumber} ({incident.SeverityDisplay}).";
            SaveHistory(uri.AbsoluteUri, $"HTTP {statusCode}", historyDetails, elapsed);

            return new()
            {
                Status = $"HTTP {statusCode}",
                BrushKey = "DangerBrush",
                Message = historyDetails,
                ResponseTime = $"{elapsed} ms",
                ResponseSummary = $"{elapsed} ms",
                ResponseTimeMs = elapsed
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            const string details = "The endpoint did not respond within the 10-second timeout.";
            return RecordFailure(uri, "Timed out", "Endpoint timed out", details,
                $"{uri.Host} timed out. Incident", "timed out", "Timeout");
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            var details = $"Could not connect to the endpoint. {exception.Message}";
            return RecordFailure(uri, "Unreachable", "Endpoint unreachable", details,
                $"{uri.Host} is unreachable. Incident", "—", "—");
        }
    }

    private AlertingSystem.Contracts.MonitoringResult RecordFailure(
        Uri uri,
        string status,
        string title,
        string details,
        string messagePrefix,
        string responseTime,
        string responseSummary)
    {
        var incident = UpsertIncident(uri, title, IncidentSeverity.High, details);
        var message = $"{messagePrefix} {incident.IncidentNumber} was created or updated.";
        SaveHistory(uri.AbsoluteUri, status,
            $"{details} Incident {incident.IncidentNumber} ({incident.SeverityDisplay}).", null);

        return new()
        {
            Status = status,
            BrushKey = "DangerBrush",
            Message = message,
            ResponseTime = responseTime,
            ResponseSummary = responseSummary
        };
    }

    private Incident UpsertIncident(
        Uri uri,
        string title,
        IncidentSeverity severity,
        string details)
    {
        var endpointKey = NormalizeEndpoint(uri.AbsoluteUri);
        var incidents = _dataStore.LoadIncidents();
        var incident = incidents.FirstOrDefault(item =>
            item.Status != IncidentStatus.Done &&
            string.Equals(NormalizeEndpoint(item.EndpointUrl), endpointKey,
                StringComparison.OrdinalIgnoreCase));

        var created = incident is null;
        if (incident is null)
        {
            incident = new Incident
            {
                IncidentNumber = $"INC-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4].ToUpperInvariant()}",
                EndpointUrl = uri.AbsoluteUri,
                Title = title,
                Severity = severity,
                Status = IncidentStatus.Open,
                Details = details,
                Reason = $"Auto-detected: {details}",
                FirstOccurredAt = DateTime.Now,
                LastOccurredAt = DateTime.Now,
                OccurrenceCount = 1
            };
        }
        else
        {
            incident.Title = title;
            incident.Details = details;
            if (string.IsNullOrWhiteSpace(incident.Reason) ||
                incident.Reason.StartsWith("Auto-detected:", StringComparison.OrdinalIgnoreCase))
            {
                incident.Reason = $"Auto-detected: {details}";
            }

            incident.LastOccurredAt = DateTime.Now;
            incident.OccurrenceCount++;
            if ((int)severity < (int)incident.Severity)
                incident.Severity = severity;
        }

        _dataStore.SaveIncident(incident, allowInsert: created);
        return incident;
    }

    private string? MarkMatchingIncidentDone(Uri uri, string recoveryDetails)
    {
        var endpointKey = NormalizeEndpoint(uri.AbsoluteUri);
        var incident = _dataStore.LoadIncidents().FirstOrDefault(item =>
            item.Status != IncidentStatus.Done &&
            string.Equals(NormalizeEndpoint(item.EndpointUrl), endpointKey,
                StringComparison.OrdinalIgnoreCase));

        if (incident is null)
            return null;

        incident.Status = IncidentStatus.Done;
        incident.ResolvedAt = DateTime.Now;
        incident.ResolutionNote =
            $"Automatically marked Done at {DateTime.Now:yyyy-MM-dd HH:mm:ss}. {recoveryDetails}";
        _dataStore.SaveIncident(incident);
        return incident.IncidentNumber;
    }

    private void SaveHistory(string endpoint, string result, string details, long? responseTimeMs)
    {
        _dataStore.SaveCheckHistoryEntry(new CheckHistoryEntry
        {
            Timestamp = DateTime.Now,
            Endpoint = endpoint,
            Result = result,
            Details = details,
            ResponseTimeMs = responseTimeMs
        });
    }

    private static IncidentSeverity GetHttpFailureSeverity(int statusCode) =>
        statusCode == 503 ? IncidentSeverity.Critical
        : statusCode >= 500 ? IncidentSeverity.High
        : statusCode >= 400 ? IncidentSeverity.Medium
        : IncidentSeverity.Low;

    private static string GetHttpFailureTitle(int statusCode) =>
        statusCode == 503 ? "Service unavailable (HTTP 503)"
        : statusCode >= 500 ? $"Server error (HTTP {statusCode})"
        : statusCode >= 400 ? $"Unexpected HTTP response (HTTP {statusCode})"
        : $"Unexpected HTTP status ({statusCode})";

    private static string NormalizeEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            ? uri.AbsoluteUri.TrimEnd('/')
            : endpoint.Trim();
}
