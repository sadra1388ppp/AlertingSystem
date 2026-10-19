using System.Diagnostics;
using System.Data.Common;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AlertingSystem.Models;
using AlertingSystem.Services;

namespace AlertingSystem;

public partial class MainWindow : Window
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly MariaDbDataStore _dataStore = new();
    private readonly List<Incident> _incidents;
    private readonly List<CheckHistoryEntry> _checkHistory;
    private int _checksRun;
    private bool _showAllIncidents;
    private bool _storageWarningShown;

    public MainWindow()
    {
        InitializeComponent();

        // Wire context-menu actions in code-behind. Keeping Click handlers out of
        // resource XAML avoids the WPF component-connection ID mismatch at startup.
        AttachContextMenuHandler(
            FindResource("IncidentListBoxItemStyle") as Style,
            DeleteIncidentMenuItem_Click);
        AttachContextMenuHandler(
            HistoryEntriesList.ItemContainerStyle,
            DeleteHistoryEntryMenuItem_Click);

        _incidents = _dataStore.LoadIncidents();

        var migratedLegacyStatuses = false;
        foreach (var incident in _incidents)
        {
            if (incident.Status != IncidentStatus.Open && incident.Status != IncidentStatus.Done)
            {
                incident.Status = IncidentStatus.Open;
                migratedLegacyStatuses = true;
            }
        }

        if (migratedLegacyStatuses)
        {
            _dataStore.SaveIncidents(_incidents);
        }

        _checkHistory = _dataStore.LoadCheckHistory()
            .OrderByDescending(entry => entry.Timestamp)
            .Take(200)
            .ToList();

        RefreshIncidentUI();
        RefreshCheckHistoryUI();
    }

    private static void AttachContextMenuHandler(Style? style, RoutedEventHandler handler)
    {
        if (style is null)
        {
            return;
        }

        var contextMenuSetter = style.Setters
            .OfType<Setter>()
            .FirstOrDefault(setter => setter.Property == FrameworkElement.ContextMenuProperty);

        if (contextMenuSetter?.Value is not ContextMenu contextMenu)
        {
            return;
        }

        foreach (var menuItem in contextMenu.Items.OfType<MenuItem>())
        {
            menuItem.Click += handler;
        }
    }

    private async void CheckEndpoint_Click(object sender, RoutedEventArgs e)
    {
        var address = EndpointTextBox.Text.Trim();

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            LastResultSummaryText.Text = "Invalid URL";
            ResponseTimeSummaryText.Text = "—";
            ResponseTimeText.Text = "Response time: —";
            SetStatus("Invalid URL", "DangerBrush");
            LastResultText.Text = "Enter a valid URL beginning with http:// or https://.";
            RecordCheck(string.IsNullOrWhiteSpace(address) ? "(empty URL)" : address,
                "Invalid URL", "Enter a valid URL beginning with http:// or https://.", null);
            IncrementChecksRun();
            return;
        }

        CheckEndpointButton.IsEnabled = false;
        CheckEndpointButton.Content = "Checking…";
        SetStatus("Checking…", "NeutralBrush");
        LastResultText.Text = $"Sending a request to {uri.Host}…";
        ResponseTimeText.Text = "Response time: checking…";

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await HttpClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead);

            stopwatch.Stop();

            var statusCode = (int)response.StatusCode;
            var elapsed = stopwatch.ElapsedMilliseconds;

            if (response.IsSuccessStatusCode)
            {
                var resolvedIncident = MarkMatchingIncidentDone(uri,
                    $"Endpoint returned HTTP {statusCode}.");

                SetStatus("Healthy", "SuccessBrush");
                LastResultSummaryText.Text = "Healthy";
                LastResultText.Text = resolvedIncident is null
                    ? $"{uri.Host} returned HTTP {statusCode} ({response.ReasonPhrase})."
                    : $"{uri.Host} recovered. Incident {resolvedIncident} was marked Done.";
                ResponseTimeText.Text = $"Response time: {elapsed} ms";
                ResponseTimeSummaryText.Text = $"{elapsed} ms";

                RecordCheck(uri.AbsoluteUri, $"HTTP {statusCode}",
                    resolvedIncident is null
                        ? $"Successful response ({response.ReasonPhrase})."
                        : $"Successful response; incident {resolvedIncident} was automatically marked Done.",
                    elapsed);
            }
            else
            {
                var severity = GetHttpFailureSeverity(statusCode);
                var title = GetHttpFailureTitle(statusCode);
                var details = $"Endpoint returned HTTP {statusCode} ({response.ReasonPhrase}).";
                var incident = UpsertIncident(uri, title, severity, details);

                SetStatus($"HTTP {statusCode}", "DangerBrush");
                LastResultSummaryText.Text = $"HTTP {statusCode}";
                LastResultText.Text = $"{details} Incident {incident.IncidentNumber} ({incident.SeverityDisplay}).";
                ResponseTimeText.Text = $"Response time: {elapsed} ms";
                ResponseTimeSummaryText.Text = $"{elapsed} ms";

                RecordCheck(uri.AbsoluteUri, $"HTTP {statusCode}",
                    $"{details} Incident {incident.IncidentNumber} ({incident.SeverityDisplay}).",
                    elapsed);
            }
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            var details = "The endpoint did not respond within the 10-second timeout.";
            var incident = UpsertIncident(uri, "Endpoint timed out", IncidentSeverity.High, details);

            SetStatus("Timed out", "DangerBrush");
            LastResultText.Text = $"{uri.Host} timed out. Incident {incident.IncidentNumber} was created or updated.";
            ResponseTimeText.Text = "Response time: timed out";
            ResponseTimeSummaryText.Text = "Timeout";
            LastResultSummaryText.Text = "Timed out";
            RecordCheck(uri.AbsoluteUri, "Timed out",
                $"{details} Incident {incident.IncidentNumber} ({incident.SeverityDisplay}).", null);
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            var details = $"Could not connect to the endpoint. {exception.Message}";
            var incident = UpsertIncident(uri, "Endpoint unreachable", IncidentSeverity.High, details);

            SetStatus("Unreachable", "DangerBrush");
            LastResultText.Text = $"{uri.Host} is unreachable. Incident {incident.IncidentNumber} was created or updated.";
            ResponseTimeText.Text = "Response time: —";
            ResponseTimeSummaryText.Text = "—";
            LastResultSummaryText.Text = "Unreachable";
            RecordCheck(uri.AbsoluteUri, "Unreachable",
                $"{details} Incident {incident.IncidentNumber} ({incident.SeverityDisplay}).", null);
        }
        finally
        {
            IncrementChecksRun();
            CheckEndpointButton.IsEnabled = true;
            CheckEndpointButton.Content = "Check endpoint";
        }
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

    private Incident UpsertIncident(Uri uri, string title, IncidentSeverity severity, string details)
    {
        var endpointKey = NormalizeEndpoint(uri);
        var incident = _incidents.FirstOrDefault(item =>
            item.Status != IncidentStatus.Done &&
            string.Equals(NormalizeEndpoint(item.EndpointUrl), endpointKey,
                StringComparison.OrdinalIgnoreCase));

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
                FirstOccurredAt = DateTime.Now,
                LastOccurredAt = DateTime.Now,
                OccurrenceCount = 1
            };

            _incidents.Add(incident);
        }
        else
        {
            incident.Title = title;
            incident.Details = details;
            incident.LastOccurredAt = DateTime.Now;
            incident.OccurrenceCount++;

            // Escalate severity when a later failure is more serious; do not silently downgrade it.
            if ((int)severity < (int)incident.Severity)
            {
                incident.Severity = severity;
            }
        }

        PersistIncidents();
        RefreshIncidentUI();
        return incident;
    }

    private string? MarkMatchingIncidentDone(Uri uri, string recoveryDetails)
    {
        var endpointKey = NormalizeEndpoint(uri);
        var incident = _incidents.FirstOrDefault(item =>
            item.Status != IncidentStatus.Done &&
            string.Equals(NormalizeEndpoint(item.EndpointUrl), endpointKey,
                StringComparison.OrdinalIgnoreCase));

        if (incident is null)
        {
            return null;
        }

        incident.Status = IncidentStatus.Done;
        incident.ResolvedAt = DateTime.Now;
        incident.ResolutionNote = $"Automatically marked Done at {DateTime.Now:yyyy-MM-dd HH:mm:ss}. {recoveryDetails}";
        PersistIncidents();
        RefreshIncidentUI();
        return incident.IncidentNumber;
    }

    private static string NormalizeEndpoint(Uri uri) => NormalizeEndpoint(uri.AbsoluteUri);

    private static string NormalizeEndpoint(string endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            ? uri.AbsoluteUri.TrimEnd('/')
            : endpoint.Trim();
    }

    private void RecordCheck(string endpoint, string result, string details, long? responseTimeMs)
    {
        _checkHistory.Insert(0, new CheckHistoryEntry
        {
            Timestamp = DateTime.Now,
            Endpoint = endpoint,
            Result = result,
            Details = details,
            ResponseTimeMs = responseTimeMs
        });

        if (_checkHistory.Count > 200)
        {
            _checkHistory.RemoveRange(200, _checkHistory.Count - 200);
        }

        PersistCheckHistory();
        RefreshCheckHistoryUI();
    }

    private void IncrementChecksRun()
    {
        _checksRun++;
        ChecksRunText.Text = _checksRun.ToString();
    }

    private void PersistIncidents()
    {
        try
        {
            _dataStore.SaveIncidents(_incidents);
        }
        catch (Exception exception) when (
            exception is DbException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ShowStorageWarning(exception);
        }
    }

    private void PersistCheckHistory()
    {
        try
        {
            _dataStore.SaveCheckHistory(_checkHistory);
        }
        catch (Exception exception) when (
            exception is DbException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ShowStorageWarning(exception);
        }
    }

    private void ShowStorageWarning(Exception exception)
    {
        if (_storageWarningShown)
        {
            return;
        }

        _storageWarningShown = true;
        MessageBox.Show(this,
            $"AlertingSystem could not save monitoring data to MariaDB. The latest change may not survive an application restart.\n\n{exception.Message}",
            "MariaDB storage warning", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void RefreshIncidentUI()
    {
        var openCount = _incidents.Count(item => item.Status == IncidentStatus.Open);
        var doneCount = _incidents.Count(item => item.Status == IncidentStatus.Done);
        OpenIncidentCountText.Text = openCount.ToString();
        TotalIncidentCountText.Text = _incidents.Count.ToString();
        DoneIncidentCountText.Text = doneCount.ToString();
        OverviewOpenIncidentsCountText.Text = $"{openCount} open incident(s)";

        var visibleIncidents = _incidents
            .Where(item => _showAllIncidents || item.Status != IncidentStatus.Done)
            .OrderByDescending(item => item.Status != IncidentStatus.Done)
            .ThenByDescending(item => item.LastOccurredAt)
            .ToList();

        IncidentsListBox.ItemsSource = visibleIncidents;
        IncidentFilterDescriptionText.Text = _showAllIncidents
            ? "Showing all incidents, including done incidents"
            : "Showing open incidents";
        IncidentsEmptyText.Text = _showAllIncidents
            ? "No incidents have been recorded yet. Run a failing endpoint check to create one."
            : "No open incidents. Run a failing endpoint check to test incident creation.";
        IncidentsEmptyText.Visibility = visibleIncidents.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        OpenIncidentsFilterButton.Background = _showAllIncidents
            ? (Brush)FindResource("SecondaryButtonBrush")
            : (Brush)FindResource("SelectedFilterBrush");
        AllIncidentsFilterButton.Background = _showAllIncidents
            ? (Brush)FindResource("SelectedFilterBrush")
            : (Brush)FindResource("SecondaryButtonBrush");

        UpdateIncidentActionButtons();
    }

    private void RefreshCheckHistoryUI()
    {
        HistoryList.Items.Clear();
        foreach (var entry in _checkHistory.Take(10))
        {
            HistoryList.Items.Add(entry.DisplayText);
        }

        HistoryEntriesList.ItemsSource = _checkHistory
            .OrderByDescending(entry => entry.Timestamp)
            .ToList();
        HistoryCountText.Text = $"{_checkHistory.Count} record(s)";
        HistoryEmptyText.Visibility = _checkHistory.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateIncidentActionButtons()
    {
        MarkDoneIncidentButton.IsEnabled =
            IncidentsListBox.SelectedItem is Incident selected &&
            selected.Status != IncidentStatus.Done;
    }

    private void SetStatus(string message, string brushKey)
    {
        CheckStatusText.Text = message;
        StatusIndicator.Fill = (Brush)FindResource(brushKey);
    }

    private void OverviewNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(OverviewPage, OverviewNavButton);
    }

    private void IncidentsNavButton_Click(object sender, RoutedEventArgs e)
    {
        _showAllIncidents = false;
        RefreshIncidentUI();
        ShowPage(IncidentsPage, IncidentsNavButton);
    }

    private void AlertHistoryNavButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshCheckHistoryUI();
        ShowPage(AlertHistoryPage, AlertHistoryNavButton);
    }

    private void ShowPage(Grid selectedPage, Button selectedButton)
    {
        OverviewPage.Visibility = Visibility.Collapsed;
        IncidentsPage.Visibility = Visibility.Collapsed;
        AlertHistoryPage.Visibility = Visibility.Collapsed;
        selectedPage.Visibility = Visibility.Visible;

        foreach (var button in new[] { OverviewNavButton, IncidentsNavButton, AlertHistoryNavButton })
        {
            button.Background = Brushes.Transparent;
            button.Foreground = (Brush)FindResource("MutedBrush");
            button.FontWeight = FontWeights.Normal;
        }

        selectedButton.Background = new SolidColorBrush(Color.FromRgb(29, 49, 82));
        selectedButton.Foreground = new SolidColorBrush(Color.FromRgb(234, 242, 255));
        selectedButton.FontWeight = FontWeights.SemiBold;
    }

    private void OpenIncidentsFilterButton_Click(object sender, RoutedEventArgs e)
    {
        _showAllIncidents = false;
        RefreshIncidentUI();
    }

    private void AllIncidentsFilterButton_Click(object sender, RoutedEventArgs e)
    {
        _showAllIncidents = true;
        RefreshIncidentUI();
    }

    private void IncidentsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateIncidentActionButtons();
    }

    private void MarkDoneIncidentButton_Click(object sender, RoutedEventArgs e)
    {
        if (IncidentsListBox.SelectedItem is not Incident selected ||
            selected.Status == IncidentStatus.Done)
        {
            return;
        }

        selected.Status = IncidentStatus.Done;
        selected.ResolvedAt = DateTime.Now;
        selected.ResolutionNote = $"Marked Done manually at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.";
        PersistIncidents();
        RefreshIncidentUI();
    }

    private void DeleteIncidentMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem ||
            menuItem.Parent is not ContextMenu contextMenu ||
            contextMenu.PlacementTarget is not ListBoxItem row ||
            row.DataContext is not Incident incident)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Permanently delete incident {incident.IncidentNumber}?\n\n{incident.Title}",
            "Delete incident",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _incidents.Remove(incident);
        PersistIncidents();
        RefreshIncidentUI();
    }

    private void DeleteHistoryEntryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem ||
            menuItem.Parent is not ContextMenu contextMenu ||
            contextMenu.PlacementTarget is not ListBoxItem row ||
            row.DataContext is not CheckHistoryEntry entry)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Permanently delete this alert-history entry?\n\n{entry.TimestampDisplay}\n{entry.Endpoint}\n{entry.Result}",
            "Delete history entry",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _checkHistory.Remove(entry);
        PersistCheckHistory();
        RefreshCheckHistoryUI();
    }
}
