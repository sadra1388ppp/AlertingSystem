using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using AlertingSystem.Models;

namespace AlertingSystem;

public partial class MainWindow
{
    private void EndpointTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;

        if (CheckEndpointButton.IsEnabled)
        {
            CheckEndpoint_Click(CheckEndpointButton, new RoutedEventArgs());
        }
    }

    private async void CheckEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckEndpointButton.IsEnabled)
        {
            return;
        }

        CheckEndpointButton.IsEnabled = false;
        CheckEndpointButtonText.Text = "Checking…";

        try
        {
            await PerformEndpointCheckAsync(EndpointTextBox.Text.Trim(), updateManualUi: true);
        }
        finally
        {
            CheckEndpointButton.IsEnabled = true;
            CheckEndpointButtonText.Text = "Check endpoint";
        }
    }

    private async Task<(string Status, long? ResponseTimeMs)> PerformEndpointCheckAsync(
        string address,
        bool updateManualUi)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            const string details = "Enter a valid URL beginning with http:// or https://.";
            if (updateManualUi)
            {
                UpdateEndpointResult("Invalid URL", "DangerBrush", details, "—", "—");
            }

            await RecordCheckAsync(string.IsNullOrWhiteSpace(address) ? "(empty URL)" : address,
                "Invalid URL", details, null);
            IncrementChecksRun();
            return ("Invalid URL", null);
        }

        if (updateManualUi)
        {
            UpdateEndpointResult("Checking…", "NeutralBrush",
                $"Sending a request to {uri.Host}…", "checking…", "Checking…");
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            stopwatch.Stop();

            var statusCode = (int)response.StatusCode;
            var elapsed = stopwatch.ElapsedMilliseconds;

            if (response.IsSuccessStatusCode)
            {
                var resolvedIncident = await MarkMatchingIncidentDoneAsync(
                    uri, $"Endpoint returned HTTP {statusCode}.");
                var details = resolvedIncident is null
                    ? $"Successful response ({response.ReasonPhrase})."
                    : $"Successful response; incident {resolvedIncident} was automatically marked Done.";

                if (updateManualUi)
                {
                    var message = resolvedIncident is null
                        ? $"{uri.Host} returned HTTP {statusCode} ({response.ReasonPhrase})."
                        : $"{uri.Host} recovered. Incident {resolvedIncident} was marked Done.";
                    UpdateEndpointResult("Healthy", "SuccessBrush", message,
                        $"{elapsed} ms", $"{elapsed} ms");
                }

                await RecordCheckAsync(uri.AbsoluteUri, $"HTTP {statusCode}", details, elapsed);
                return ("Healthy", elapsed);
            }

            var detailsForFailure = $"Endpoint returned HTTP {statusCode} ({response.ReasonPhrase}).";
            var incident = await UpsertIncidentAsync(
                uri, GetHttpFailureTitle(statusCode), GetHttpFailureSeverity(statusCode), detailsForFailure);
            var historyDetails = $"{detailsForFailure} Incident {incident.IncidentNumber} ({incident.SeverityDisplay}).";

            if (updateManualUi)
            {
                UpdateEndpointResult($"HTTP {statusCode}", "DangerBrush", historyDetails,
                    $"{elapsed} ms", $"{elapsed} ms");
            }

            await RecordCheckAsync(uri.AbsoluteUri, $"HTTP {statusCode}", historyDetails, elapsed);
            return ($"HTTP {statusCode}", elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            const string details = "The endpoint did not respond within the 10-second timeout.";
            return await RecordEndpointFailureAsync(uri, "Timed out", "Endpoint timed out", details,
                updateManualUi,
                incident => $"{uri.Host} timed out. Incident {incident.IncidentNumber} was created or updated.",
                "timed out", "Timeout");
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            var details = $"Could not connect to the endpoint. {exception.Message}";
            return await RecordEndpointFailureAsync(uri, "Unreachable", "Endpoint unreachable", details,
                updateManualUi,
                incident => $"{uri.Host} is unreachable. Incident {incident.IncidentNumber} was created or updated.",
                "—", "—");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            var details = $"The check could not be completed. {exception.Message}";
            return await RecordEndpointFailureAsync(uri, "Check failed", "Endpoint check failed", details,
                updateManualUi,
                incident => $"{details} Incident {incident.IncidentNumber} was created or updated.",
                "—", "—");
        }
        finally
        {
            IncrementChecksRun();
        }
    }

    private async Task<(string Status, long? ResponseTimeMs)> RecordEndpointFailureAsync(
        Uri uri,
        string status,
        string title,
        string details,
        bool updateManualUi,
        Func<Incident, string> message,
        string responseTime,
        string responseSummary)
    {
        var incident = await UpsertIncidentAsync(uri, title, IncidentSeverity.High, details);
        if (updateManualUi)
        {
            UpdateEndpointResult(status, "DangerBrush", message(incident), responseTime, responseSummary);
        }

        await RecordCheckAsync(uri.AbsoluteUri, status,
            $"{details} Incident {incident.IncidentNumber} ({incident.SeverityDisplay}).", null);
        return (status, null);
    }

    private void UpdateEndpointResult(
        string status,
        string brushKey,
        string message,
        string responseTime,
        string responseSummary)
    {
        SetStatus(status, brushKey);
        LastResultSummaryText.Text = status;
        LastResultText.Text = message;
        ResponseTimeText.Text = $"Response time: {responseTime}";
        ResponseTimeSummaryText.Text = responseSummary;
    }
}
