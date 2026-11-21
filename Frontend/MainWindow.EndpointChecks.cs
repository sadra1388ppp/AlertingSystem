using System.Windows;
using AlertingSystem.Contracts;
using AlertingSystem.Services;

namespace AlertingSystem;

public partial class MainWindow
{
    private void EndpointTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
            return;

        e.Handled = true;
        if (CheckEndpointButton.IsEnabled)
            CheckEndpoint_Click(CheckEndpointButton, new RoutedEventArgs());
    }

    private async void CheckEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckEndpointButton.IsEnabled)
            return;

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
        try
        {
            var result = await Task.Run(() =>
                BackendApiClient.Post<MonitoringResult>(
                    "api/monitoring/check",
                    new MonitoringCheckRequest { Address = address }));

            if (updateManualUi)
            {
                UpdateEndpointResult(
                    result.Status,
                    result.BrushKey,
                    result.Message,
                    result.ResponseTime,
                    result.ResponseSummary);
            }

            await SyncFromDatabaseAsync();
            IncrementChecksRun();
            return (result.Status, result.ResponseTimeMs);
        }
        catch (Exception exception)
        {
            const string status = "Backend unavailable";
            if (updateManualUi)
            {
                UpdateEndpointResult(
                    status,
                    "DangerBrush",
                    $"The endpoint check could not be completed because the backend request failed. {exception.Message}",
                    "—",
                    "—");
            }

            IncrementChecksRun();
            return (status, null);
        }
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
