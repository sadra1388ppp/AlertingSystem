using System.Diagnostics;
using System.Data.Common;
using System.IO;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AlertingSystem.Models;
using AlertingSystem.Services;
using Microsoft.EntityFrameworkCore;
using System.Windows.Threading;

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
    private readonly HiddenItemsStore _hiddenItems = new();
    private readonly ScheduledEndpointStore _scheduledEndpointStore = new();
    private readonly ObservableCollection<ScheduledEndpointStatus> _scheduledEndpointStatuses = [];
    private ScheduleConfiguration _scheduleConfiguration = new();
    private readonly DispatcherTimer _scheduledCheckTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private bool _scheduledBatchRunning;
    private bool _scheduleIntervalIsMinutes = true;
    private string _lastScheduledScanSummary = string.Empty;
    private readonly DispatcherTimer _databaseSyncTimer = new()
    {
        Interval = TimeSpan.FromSeconds(3)
    };
    private int _checksRun;
    private bool _showAllIncidents;
    private bool _isUpdatingIncidentDetails;
    private string? _displayedIncidentNumber;
    private bool _storageWarningShown;
    private bool _isLightTheme;
    private bool _isUpdatingThemeOptions;
    private string _selectedTheme = "Dark";
    private string _currentStatusBrushKey = "NeutralBrush";
    private Grid? _activePage;
    private Button? _activeNavButton;

    private static readonly Dictionary<string, (string Dark, string Light)> ThemeRoleColors = new()
    {
        ["AppBackgroundBrush"] = ("#0A1020", "#F4F7FB"),
        ["SidebarBrush"] = ("#0D1628", "#EFF3F9"),
        ["CardBrush"] = ("#121E33", "#FFFFFF"),
        ["BorderBrush"] = ("#263650", "#DCE4EF"),
        ["TextBrush"] = ("#F1F5FC", "#182338"),
        ["MutedBrush"] = ("#9AAAC2", "#68758C"),
        ["SuccessBrush"] = ("#3DD9A0", "#16845D"),
        ["CriticalBrush"] = ("#C084FC", "#7C3AED"),
        ["WarningBrush"] = ("#F6C65B", "#9A6700"),
        ["DangerBrush"] = ("#FF718B", "#C52F4B"),
        ["NeutralBrush"] = ("#72A7FF", "#2F65D5"),
        ["SecondaryButtonBrush"] = ("#1B2A44", "#E8EDF6"),
        ["SelectedFilterBrush"] = ("#477DFA", "#2F63E0")
    };

    private static readonly string[] ThemeLiteralColors =
    {
        "#0A1020",
        "#0B1120",
        "#0B1425",
        "#101A2B",
        "#101B2F",
        "#14233A",
        "#162743",
        "#18253B",
        "#1A2942",
        "#20385F",
        "#294568",
        "#302713",
        "#3B82F6",
        "#477DFA",
        "#5C8CFF",
        "#64748B",
        "#71829C",
        "#75602A",
        "#91A2BB",
        "#D8E4F5",
        "#EAF2FF",
        "#EEF3FA",
        "#FDE68A"
    };

    private static readonly Dictionary<string, string> LightLiteralColorOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#0A1020"] = "#F4F7FB",
        ["#477DFA"] = "#2F63E0",
        ["#5C8CFF"] = "#2F63E0",
        ["#91A2BB"] = "#68758C",
        ["#18253B"] = "#E7ECF5",
        ["#EEF3FA"] = "#182338",
        ["#1A2942"] = "#E8EDF6",
        ["#D8E4F5"] = "#2D3B53",
        ["#14233A"] = "#E5EDFC",
        ["#3B82F6"] = "#2F63E0",
        ["#0B1425"] = "#F7F9FC",
        ["#71829C"] = "#748198",
        ["#20385F"] = "#DDE8FF",
        ["#EAF2FF"] = "#244A9B",
        ["#64748B"] = "#69778D",
        ["#75602A"] = "#D6C17D",
        ["#302713"] = "#FFF5D8",
        ["#FDE68A"] = "#845A00",
        ["#162743"] = "#E9F1FF",
        ["#294568"] = "#C7D7F0",
        ["#101B2F"] = "#FFFFFF",
        ["#0B1120"] = "#182338",
        ["#101A2B"] = "#FFFFFF"
    };

    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        LoadThemePreference();
        ShowPage(OverviewPage, OverviewNavButton);

        // Wire context-menu actions in code-behind. Keeping Click handlers out of
        // resource XAML avoids the WPF component-connection ID mismatch at startup.
        AttachContextMenuHandler(
            FindResource("IncidentListBoxItemStyle") as Style,
            DeleteIncidentMenuItem_Click);
        AttachContextMenuHandler(
            HistoryEntriesList.ItemContainerStyle,
            DeleteHistoryEntryMenuItem_Click);

        var databaseIncidents = _dataStore.LoadIncidents();
        var databaseHistory = _dataStore.LoadCheckHistory()
            .OrderByDescending(entry => entry.Timestamp)
            .Take(200)
            .ToList();

        _hiddenItems.Reconcile(databaseIncidents, _dataStore.LoadCheckHistoryIds());

        var migratedIncidentData = false;
        foreach (var incident in databaseIncidents)
        {
            if (incident.Status != IncidentStatus.Open && incident.Status != IncidentStatus.Done)
            {
                incident.Status = IncidentStatus.Open;
                migratedIncidentData = true;
            }

            // Backfill a useful initial reason for incidents created by older app versions.
            if (string.IsNullOrWhiteSpace(incident.Reason))
            {
                incident.Reason = $"Auto-detected from failure details: {incident.Details}";
                migratedIncidentData = true;
            }
        }

        if (migratedIncidentData)
        {
            _dataStore.SaveIncidents(databaseIncidents);
        }

        _incidents = databaseIncidents
            .Where(incident => !_hiddenItems.IsIncidentHidden(incident.IncidentNumber))
            .ToList();

        _checkHistory = databaseHistory
            .Where(entry => !_hiddenItems.IsHistoryEntryHidden(entry.Id))
            .ToList();

        IncidentSearchTextBox.TextChanged += IncidentSearchTextBox_TextChanged;
        IncidentSeverityFilterComboBox.SelectionChanged += IncidentSeverityFilterComboBox_SelectionChanged;
        IncidentSortComboBox.SelectionChanged += IncidentSortComboBox_SelectionChanged;
        SelectedIncidentSeverityComboBox.SelectionChanged += SelectedIncidentSeverityComboBox_SelectionChanged;
        IncidentReasonTextBox.TextChanged += IncidentReasonTextBox_TextChanged;
        IncidentResolutionTextBox.TextChanged += IncidentResolutionTextBox_TextChanged;

        RefreshIncidentUI();
        RefreshCheckHistoryUI();

        _databaseSyncTimer.Tick += (_, _) => SyncFromDatabase();
        _databaseSyncTimer.Start();
        _scheduledCheckTimer.Tick += ScheduledCheckTimer_Tick;
        InitializeScheduledChecks();
        Closed += (_, _) =>
        {
            _databaseSyncTimer.Stop();
            _scheduledCheckTimer.Stop();
        };
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

    private void EndpointTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;

        // Reuse the exact same validation and check flow as the button.
        // Ignore further Enter presses while a check is already running.
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
        CheckEndpointButton.Content = "Checking…";

        try
        {
            await PerformEndpointCheckAsync(EndpointTextBox.Text.Trim(), updateManualUi: true);
        }
        finally
        {
            CheckEndpointButton.IsEnabled = true;
            CheckEndpointButton.Content = "Check endpoint";
        }
    }

    /// <summary>
    /// Shared check pipeline for manual and scheduled checks. Both paths create history entries,
    /// update incidents, and use the same HTTP status/timeout rules; only manual checks alter
    /// the large result panel on the Endpoint checker page.
    /// </summary>
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
                SetStatus("Invalid URL", "DangerBrush");
                LastResultSummaryText.Text = "Invalid URL";
                LastResultText.Text = details;
                ResponseTimeText.Text = "Response time: —";
                ResponseTimeSummaryText.Text = "—";
            }

            RecordCheck(string.IsNullOrWhiteSpace(address) ? "(empty URL)" : address,
                "Invalid URL", details, null);
            IncrementChecksRun();
            return ("Invalid URL", null);
        }

        if (updateManualUi)
        {
            SetStatus("Checking…", "NeutralBrush");
            LastResultSummaryText.Text = "Checking…";
            LastResultText.Text = $"Sending a request to {uri.Host}…";
            ResponseTimeText.Text = "Response time: checking…";
            ResponseTimeSummaryText.Text = "Checking…";
        }

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
                var details = resolvedIncident is null
                    ? $"Successful response ({response.ReasonPhrase})."
                    : $"Successful response; incident {resolvedIncident} was automatically marked Done.";

                if (updateManualUi)
                {
                    SetStatus("Healthy", "SuccessBrush");
                    LastResultSummaryText.Text = "Healthy";
                    LastResultText.Text = resolvedIncident is null
                        ? $"{uri.Host} returned HTTP {statusCode} ({response.ReasonPhrase})."
                        : $"{uri.Host} recovered. Incident {resolvedIncident} was marked Done.";
                    ResponseTimeText.Text = $"Response time: {elapsed} ms";
                    ResponseTimeSummaryText.Text = $"{elapsed} ms";
                }

                RecordCheck(uri.AbsoluteUri, $"HTTP {statusCode}", details, elapsed);
                return ("Healthy", elapsed);
            }

            var severity = GetHttpFailureSeverity(statusCode);
            var title = GetHttpFailureTitle(statusCode);
            var responseDetails = $"Endpoint returned HTTP {statusCode} ({response.ReasonPhrase}).";
            var incident = UpsertIncident(uri, title, severity, responseDetails);
            var historyDetails = $"{responseDetails} Incident {incident.IncidentNumber} ({incident.SeverityDisplay}).";

            if (updateManualUi)
            {
                SetStatus($"HTTP {statusCode}", "DangerBrush");
                LastResultSummaryText.Text = $"HTTP {statusCode}";
                LastResultText.Text = $"{responseDetails} Incident {incident.IncidentNumber} ({incident.SeverityDisplay}).";
                ResponseTimeText.Text = $"Response time: {elapsed} ms";
                ResponseTimeSummaryText.Text = $"{elapsed} ms";
            }

            RecordCheck(uri.AbsoluteUri, $"HTTP {statusCode}", historyDetails, elapsed);
            return ($"HTTP {statusCode}", elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            const string details = "The endpoint did not respond within the 10-second timeout.";
            var incident = UpsertIncident(uri, "Endpoint timed out", IncidentSeverity.High, details);
            var historyDetails = $"{details} Incident {incident.IncidentNumber} ({incident.SeverityDisplay}).";

            if (updateManualUi)
            {
                SetStatus("Timed out", "DangerBrush");
                LastResultText.Text = $"{uri.Host} timed out. Incident {incident.IncidentNumber} was created or updated.";
                ResponseTimeText.Text = "Response time: timed out";
                ResponseTimeSummaryText.Text = "Timeout";
                LastResultSummaryText.Text = "Timed out";
            }

            RecordCheck(uri.AbsoluteUri, "Timed out", historyDetails, null);
            return ("Timed out", null);
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            var details = $"Could not connect to the endpoint. {exception.Message}";
            var incident = UpsertIncident(uri, "Endpoint unreachable", IncidentSeverity.High, details);
            var historyDetails = $"{details} Incident {incident.IncidentNumber} ({incident.SeverityDisplay}).";

            if (updateManualUi)
            {
                SetStatus("Unreachable", "DangerBrush");
                LastResultText.Text = $"{uri.Host} is unreachable. Incident {incident.IncidentNumber} was created or updated.";
                ResponseTimeText.Text = "Response time: —";
                ResponseTimeSummaryText.Text = "—";
                LastResultSummaryText.Text = "Unreachable";
            }

            RecordCheck(uri.AbsoluteUri, "Unreachable", historyDetails, null);
            return ("Unreachable", null);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            var details = $"The check could not be completed. {exception.Message}";
            var incident = UpsertIncident(uri, "Endpoint check failed", IncidentSeverity.High, details);
            var historyDetails = $"{details} Incident {incident.IncidentNumber} ({incident.SeverityDisplay}).";

            if (updateManualUi)
            {
                SetStatus("Check failed", "DangerBrush");
                LastResultSummaryText.Text = "Check failed";
                LastResultText.Text = $"{details} Incident {incident.IncidentNumber} was created or updated.";
                ResponseTimeText.Text = "Response time: —";
                ResponseTimeSummaryText.Text = "—";
            }

            RecordCheck(uri.AbsoluteUri, "Check failed", historyDetails, null);
            return ("Check failed", null);
        }
        finally
        {
            IncrementChecksRun();
        }
    }

    private void InitializeScheduledChecks()
    {
        _scheduleConfiguration = _scheduledEndpointStore.Load();
        ScheduleEndpointsTextBox.Text = string.Join(Environment.NewLine, _scheduleConfiguration.Endpoints);
        SetScheduleIntervalControls(_scheduleConfiguration.IntervalSeconds);
        ScheduledEndpointsList.ItemsSource = _scheduledEndpointStatuses;
        RebuildScheduledEndpointStatuses(_scheduleConfiguration.Endpoints);

        if (_scheduleConfiguration.IsEnabled && _scheduleConfiguration.Endpoints.Count > 0)
        {
            _scheduleConfiguration.NextRunAt ??= DateTime.Now.AddSeconds(_scheduleConfiguration.IntervalSeconds);
            _scheduledCheckTimer.Start();
        }
        else if (_scheduleConfiguration.IsEnabled)
        {
            _scheduleConfiguration.IsEnabled = false;
            _scheduleConfiguration.NextRunAt = null;
            SaveScheduleConfigurationSafely();
        }

        UpdateScheduledChecksUI();
    }

    private void SetScheduleIntervalControls(int intervalSeconds)
    {
        _scheduleIntervalIsMinutes = intervalSeconds >= 60 && intervalSeconds % 60 == 0;
        ScheduleIntervalTextBox.Text = (_scheduleIntervalIsMinutes
            ? intervalSeconds / 60
            : intervalSeconds).ToString();
        UpdateScheduleUnitSelector();
    }

    private void ScheduleSecondsUnitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scheduledBatchRunning)
        {
            return;
        }

        _scheduleIntervalIsMinutes = false;
        UpdateScheduleUnitSelector();
    }

    private void ScheduleMinutesUnitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scheduledBatchRunning)
        {
            return;
        }

        _scheduleIntervalIsMinutes = true;
        UpdateScheduleUnitSelector();
    }

    private void UpdateScheduleUnitSelector()
    {
        var selectedBackground = (Brush)FindResource("Hex20385FBrush");
        var selectedBorder = (Brush)FindResource("Hex477DFABrush");
        var selectedForeground = (Brush)FindResource("HexEAF2FFBrush");
        var idleForeground = (Brush)FindResource("MutedBrush");

        ScheduleSecondsUnitButton.Background = _scheduleIntervalIsMinutes
            ? Brushes.Transparent
            : selectedBackground;
        ScheduleSecondsUnitButton.BorderBrush = _scheduleIntervalIsMinutes
            ? Brushes.Transparent
            : selectedBorder;
        ScheduleSecondsUnitButton.Foreground = _scheduleIntervalIsMinutes
            ? idleForeground
            : selectedForeground;
        ScheduleSecondsUnitButton.FontWeight = _scheduleIntervalIsMinutes
            ? FontWeights.Medium
            : FontWeights.SemiBold;

        ScheduleMinutesUnitButton.Background = _scheduleIntervalIsMinutes
            ? selectedBackground
            : Brushes.Transparent;
        ScheduleMinutesUnitButton.BorderBrush = _scheduleIntervalIsMinutes
            ? selectedBorder
            : Brushes.Transparent;
        ScheduleMinutesUnitButton.Foreground = _scheduleIntervalIsMinutes
            ? selectedForeground
            : idleForeground;
        ScheduleMinutesUnitButton.FontWeight = _scheduleIntervalIsMinutes
            ? FontWeights.SemiBold
            : FontWeights.Medium;
    }

    private static string FormatInterval(int intervalSeconds)
    {
        if (intervalSeconds >= 60 && intervalSeconds % 60 == 0)
        {
            var minutes = intervalSeconds / 60;
            return $"{minutes} minute{(minutes == 1 ? string.Empty : "s")}";
        }

        return $"{intervalSeconds} second{(intervalSeconds == 1 ? string.Empty : "s")}";
    }

    private static string FormatNextRun(DateTime nextRun)
    {
        var remaining = nextRun - DateTime.Now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var countdown = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";

        return $"In {countdown}  •  {nextRun:HH:mm:ss}";
    }

    private void SaveScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (SaveScheduleInputs())
        {
            MessageBox.Show(this,
                "Your endpoint list and interval have been saved on this computer.",
                "Schedule saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private bool SaveScheduleInputs()
    {
        var rawEndpoints = ScheduleEndpointsTextBox.Text
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (rawEndpoints.Count == 0)
        {
            MessageBox.Show(this, "Add at least one URL or domain, with one endpoint per line.",
                "No endpoints", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var endpoints = new List<string>();
        var invalidEndpoints = new List<string>();
        foreach (var raw in rawEndpoints)
        {
            if (!TryNormalizeScheduledEndpoint(raw, out var normalized))
            {
                invalidEndpoints.Add(raw);
                continue;
            }

            if (!endpoints.Contains(normalized, StringComparer.Ordinal))
            {
                endpoints.Add(normalized);
            }
        }

        if (invalidEndpoints.Count > 0)
        {
            MessageBox.Show(this,
                "These entries are not valid HTTP/HTTPS URLs or domains:\n\n" +
                string.Join(Environment.NewLine, invalidEndpoints.Take(10)) +
                (invalidEndpoints.Count > 10 ? "\n…" : string.Empty),
                "Check endpoint list", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!int.TryParse(ScheduleIntervalTextBox.Text.Trim(), out var intervalValue) ||
            intervalValue < 1)
        {
            MessageBox.Show(this, "Enter a positive whole number for the check interval.",
                "Invalid interval", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var intervalIsMinutes = _scheduleIntervalIsMinutes;
        var maximumValue = intervalIsMinutes ? 1440 : 86400;
        if (intervalValue > maximumValue)
        {
            MessageBox.Show(this,
                intervalIsMinutes
                    ? "The maximum interval is 1440 minutes (24 hours)."
                    : "The maximum interval is 86400 seconds (24 hours).",
                "Invalid interval", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var intervalSeconds = intervalIsMinutes ? intervalValue * 60 : intervalValue;
        _scheduleConfiguration.Endpoints = endpoints;
        _scheduleConfiguration.IntervalSeconds = intervalSeconds;
        if (_scheduleConfiguration.IsEnabled)
        {
            _scheduleConfiguration.NextRunAt = DateTime.Now.AddSeconds(intervalSeconds);
        }

        ScheduleEndpointsTextBox.Text = string.Join(Environment.NewLine, endpoints);
        RebuildScheduledEndpointStatuses(endpoints);

        if (!SaveScheduleConfigurationSafely())
        {
            return false;
        }

        UpdateScheduledChecksUI();
        return true;
    }

    private static bool TryNormalizeScheduledEndpoint(string endpoint, out string normalized)
    {
        normalized = string.Empty;
        var candidate = endpoint.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"https://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
    }

    private void RebuildScheduledEndpointStatuses(IEnumerable<string> endpoints)
    {
        var previousStatuses = _scheduledEndpointStatuses.ToDictionary(
            item => item.Endpoint, StringComparer.Ordinal);
        var recentHistory = _checkHistory
            .OrderByDescending(entry => entry.Timestamp)
            .ToList();

        _scheduledEndpointStatuses.Clear();
        foreach (var endpoint in endpoints.Distinct(StringComparer.Ordinal))
        {
            if (previousStatuses.TryGetValue(endpoint, out var previous))
            {
                _scheduledEndpointStatuses.Add(previous);
                continue;
            }

            var status = new ScheduledEndpointStatus(endpoint);
            var latestCheck = recentHistory.FirstOrDefault(entry =>
                string.Equals(
                    NormalizeEndpoint(entry.Endpoint),
                    NormalizeEndpoint(endpoint),
                    StringComparison.OrdinalIgnoreCase));

            if (latestCheck is not null)
            {
                status.LastResult = latestCheck.Result.StartsWith("HTTP 2", StringComparison.OrdinalIgnoreCase)
                    ? "Healthy"
                    : latestCheck.Result;
                status.LastCheckedAt = latestCheck.Timestamp;
                status.ResponseTimeMs = latestCheck.ResponseTimeMs;
            }

            _scheduledEndpointStatuses.Add(status);
        }

        ScheduledEndpointCountText.Text = $"{_scheduledEndpointStatuses.Count} endpoint(s)";
        ScheduledEndpointsEmptyText.Visibility = _scheduledEndpointStatuses.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void StartScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scheduledBatchRunning || !SaveScheduleInputs())
        {
            return;
        }

        _scheduleConfiguration.IsEnabled = true;
        _scheduleConfiguration.NextRunAt = DateTime.Now.AddSeconds(_scheduleConfiguration.IntervalSeconds);
        if (!SaveScheduleConfigurationSafely())
        {
            _scheduleConfiguration.IsEnabled = false;
            _scheduleConfiguration.NextRunAt = null;
            UpdateScheduledChecksUI();
            return;
        }

        _lastScheduledScanSummary = string.Empty;
        _scheduledCheckTimer.Start();
        UpdateScheduledChecksUI();
    }

    private void PauseScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scheduledBatchRunning)
        {
            return;
        }

        _scheduleConfiguration.IsEnabled = false;
        _scheduleConfiguration.NextRunAt = null;
        _scheduledCheckTimer.Stop();
        SaveScheduleConfigurationSafely();
        UpdateScheduledChecksUI();
    }

    private async void RunScheduledNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scheduledBatchRunning || !SaveScheduleInputs())
        {
            return;
        }

        await RunScheduledBatchAsync();
    }

    private async void ScheduledCheckTimer_Tick(object? sender, EventArgs e)
    {
        if (!_scheduleConfiguration.IsEnabled || _scheduledBatchRunning)
        {
            return;
        }

        if (_scheduledEndpointStatuses.Count == 0)
        {
            _scheduleConfiguration.IsEnabled = false;
            _scheduleConfiguration.NextRunAt = null;
            _scheduledCheckTimer.Stop();
            SaveScheduleConfigurationSafely();
            UpdateScheduledChecksUI();
            return;
        }

        if (_scheduleConfiguration.NextRunAt is null)
        {
            _scheduleConfiguration.NextRunAt = DateTime.Now.AddSeconds(_scheduleConfiguration.IntervalSeconds);
            SaveScheduleConfigurationSafely();
            UpdateScheduledChecksUI();
            return;
        }

        if (DateTime.Now >= _scheduleConfiguration.NextRunAt.Value)
        {
            await RunScheduledBatchAsync();
        }
        else
        {
            ScheduleNextRunText.Text = FormatNextRun(_scheduleConfiguration.NextRunAt.Value);
        }
    }

    private async Task RunScheduledBatchAsync()
    {
        if (_scheduledBatchRunning)
        {
            return;
        }

        var endpoints = _scheduledEndpointStatuses.ToList();
        if (endpoints.Count == 0)
        {
            MessageBox.Show(this, "Save at least one endpoint before running a scan.",
                "No endpoints", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _scheduledBatchRunning = true;
        UpdateScheduledChecksUI();

        try
        {
            for (var index = 0; index < endpoints.Count; index++)
            {
                var endpoint = endpoints[index];
                ScheduleStatusText.Text = $"Checking {index + 1} of {endpoints.Count}: {endpoint.Endpoint}";
                endpoint.LastResult = "Checking…";

                var outcome = await PerformEndpointCheckAsync(endpoint.Endpoint, updateManualUi: false);
                endpoint.LastResult = outcome.Status;
                endpoint.LastCheckedAt = DateTime.Now;
                endpoint.ResponseTimeMs = outcome.ResponseTimeMs;
            }

            _lastScheduledScanSummary = $"Last scan completed at {DateTime.Now:HH:mm:ss} ({endpoints.Count} endpoints checked).";
        }
        catch (Exception exception)
        {
            _lastScheduledScanSummary = $"Scan stopped after an unexpected error: {exception.Message}";
        }
        finally
        {
            _scheduledBatchRunning = false;

            if (_scheduleConfiguration.IsEnabled)
            {
                _scheduleConfiguration.NextRunAt = DateTime.Now.AddSeconds(_scheduleConfiguration.IntervalSeconds);
            }

            SaveScheduleConfigurationSafely();
            UpdateScheduledChecksUI();
        }
    }

    private bool SaveScheduleConfigurationSafely()
    {
        try
        {
            _scheduledEndpointStore.Save(_scheduleConfiguration);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this,
                $"The schedule could not be saved locally. Your current session can continue, but this change may not survive an app restart.\n\n{exception.Message}",
                "Schedule storage warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void UpdateScheduledChecksUI()
    {
        ScheduledEndpointCountText.Text = $"{_scheduledEndpointStatuses.Count} endpoint(s)";
        ScheduledEndpointsEmptyText.Visibility = _scheduledEndpointStatuses.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        SaveScheduleButton.IsEnabled = !_scheduledBatchRunning;
        ScheduleEndpointsTextBox.IsEnabled = !_scheduledBatchRunning;
        ScheduleIntervalTextBox.IsEnabled = !_scheduledBatchRunning;
        ScheduleSecondsUnitButton.IsEnabled = !_scheduledBatchRunning;
        ScheduleMinutesUnitButton.IsEnabled = !_scheduledBatchRunning;
        RunScheduledNowButton.IsEnabled = !_scheduledBatchRunning && _scheduledEndpointStatuses.Count > 0;
        StartScheduleButton.IsEnabled = !_scheduledBatchRunning && !_scheduleConfiguration.IsEnabled;
        PauseScheduleButton.IsEnabled = !_scheduledBatchRunning && _scheduleConfiguration.IsEnabled;

        if (_scheduledBatchRunning)
        {
            ScheduleStatusBadgeText.Text = "SCANNING";
            ScheduleStatusBadgeText.Foreground = (Brush)FindResource("HexEAF2FFBrush");
            ScheduleStatusIndicator.Fill = (Brush)FindResource("NeutralBrush");
            ScheduleNextRunText.Text = "Scan in progress";
            return;
        }

        if (_scheduleConfiguration.IsEnabled)
        {
            ScheduleStatusBadgeText.Text = "ACTIVE";
            ScheduleStatusBadgeText.Foreground = (Brush)FindResource("SuccessBrush");
            ScheduleStatusIndicator.Fill = (Brush)FindResource("SuccessBrush");
            ScheduleNextRunText.Text = _scheduleConfiguration.NextRunAt is DateTime nextRun
                ? FormatNextRun(nextRun)
                : "Calculating…";
            ScheduleStatusText.Text = string.IsNullOrWhiteSpace(_lastScheduledScanSummary)
                ? $"Automatic checks are active every {FormatInterval(_scheduleConfiguration.IntervalSeconds)} after the previous scan finishes."
                : _lastScheduledScanSummary;
        }
        else
        {
            ScheduleStatusBadgeText.Text = "PAUSED";
            ScheduleStatusBadgeText.Foreground = (Brush)FindResource("MutedBrush");
            ScheduleStatusIndicator.Fill = (Brush)FindResource("WarningBrush");
            ScheduleNextRunText.Text = "Not scheduled";
            ScheduleStatusText.Text = _scheduledEndpointStatuses.Count == 0
                ? "Add endpoints and save the list to configure monitoring."
                : "Schedule paused. Use Run all now to run a one-time scan.";
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

        var created = incident is null;
        if (created)
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

            _incidents.Add(incident);
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

            // Escalate severity when a later failure is more serious; do not silently downgrade it.
            if ((int)severity < (int)incident.Severity)
            {
                incident.Severity = severity;
            }
        }

        PersistIncident(incident, allowInsert: created);
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
        PersistIncident(incident);
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
        var entry = new CheckHistoryEntry
        {
            Timestamp = DateTime.Now,
            Endpoint = endpoint,
            Result = result,
            Details = details,
            ResponseTimeMs = responseTimeMs
        };
        _checkHistory.Insert(0, entry);

        if (_checkHistory.Count > 200)
        {
            _checkHistory.RemoveRange(200, _checkHistory.Count - 200);
        }

        PersistCheckHistory(entry);
        RefreshCheckHistoryUI();
    }

    private void IncrementChecksRun()
    {
        _checksRun++;
        ChecksRunText.Text = _checksRun.ToString();
    }

    private bool PersistIncident(Incident incident, bool allowInsert = false)
    {
        try
        {
            return _dataStore.SaveIncident(incident, allowInsert);
        }
        catch (Exception exception) when (
            exception is DbException or DbUpdateException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ShowStorageWarning(exception);
            return false;
        }
    }

    private void PersistCheckHistory(CheckHistoryEntry entry)
    {
        try
        {
            _dataStore.SaveCheckHistoryEntry(entry);
        }
        catch (Exception exception) when (
            exception is DbException or DbUpdateException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
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

    private void SyncFromDatabase()
    {
        try
        {
            var databaseIncidents = _dataStore.LoadIncidents();
            var databaseHistory = _dataStore.LoadCheckHistory()
                .OrderByDescending(entry => entry.Timestamp)
                .Take(200)
                .ToList();

            _hiddenItems.Reconcile(databaseIncidents, _dataStore.LoadCheckHistoryIds());

            var visibleIncidents = databaseIncidents
                .Where(incident => !_hiddenItems.IsIncidentHidden(incident.IncidentNumber))
                .ToList();
            var visibleHistory = databaseHistory
                .Where(entry => !_hiddenItems.IsHistoryEntryHidden(entry.Id))
                .ToList();

            if (!IncidentListsEqual(_incidents, visibleIncidents))
            {
                _incidents.Clear();
                _incidents.AddRange(visibleIncidents);
                RefreshIncidentUI();
            }

            if (!HistoryListsEqual(_checkHistory, visibleHistory))
            {
                _checkHistory.Clear();
                _checkHistory.AddRange(visibleHistory);
                RefreshCheckHistoryUI();
            }
        }
        catch (Exception exception)
        {
            ShowStorageWarning(exception);
        }
    }

    private static bool IncidentListsEqual(
        IReadOnlyCollection<Incident> current,
        IReadOnlyCollection<Incident> incoming)
    {
        if (current.Count != incoming.Count)
        {
            return false;
        }

        var currentByNumber = current.ToDictionary(
            incident => incident.IncidentNumber,
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in incoming)
        {
            if (!currentByNumber.TryGetValue(item.IncidentNumber, out var existing) ||
                existing.EndpointUrl != item.EndpointUrl ||
                existing.Title != item.Title ||
                existing.Details != item.Details ||
                existing.ResolutionNote != item.ResolutionNote ||
                existing.Reason != item.Reason ||
                existing.Severity != item.Severity ||
                existing.Status != item.Status ||
                existing.FirstOccurredAt != item.FirstOccurredAt ||
                existing.LastOccurredAt != item.LastOccurredAt ||
                existing.ResolvedAt != item.ResolvedAt ||
                existing.OccurrenceCount != item.OccurrenceCount)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HistoryListsEqual(
        IReadOnlyCollection<CheckHistoryEntry> current,
        IReadOnlyCollection<CheckHistoryEntry> incoming)
    {
        if (current.Count != incoming.Count)
        {
            return false;
        }

        var currentById = current.ToDictionary(entry => entry.Id);

        foreach (var item in incoming)
        {
            if (!currentById.TryGetValue(item.Id, out var existing) ||
                existing.Timestamp != item.Timestamp ||
                existing.Endpoint != item.Endpoint ||
                existing.Result != item.Result ||
                existing.Details != item.Details ||
                existing.ResponseTimeMs != item.ResponseTimeMs)
            {
                return false;
            }
        }

        return true;
    }

    private void RefreshIncidentUI()
    {
        var searchText = IncidentSearchTextBox.Text;
        var hasSearchText = !string.IsNullOrWhiteSpace(searchText);
        IncidentSearchPlaceholderText.Visibility = hasSearchText ? Visibility.Collapsed : Visibility.Visible;
        ClearIncidentSearchButton.Visibility = hasSearchText ? Visibility.Visible : Visibility.Collapsed;

        var openIncidents = _incidents.Where(item => item.Status == IncidentStatus.Open).ToList();
        var openCount = openIncidents.Count;
        var doneCount = _incidents.Count(item => item.Status == IncidentStatus.Done);
        var criticalCount = openIncidents.Count(item => item.Severity == IncidentSeverity.Critical);
        OpenIncidentCountText.Text = openCount.ToString();
        CriticalIncidentCountText.Text = criticalCount.ToString();
        TotalIncidentCountText.Text = _incidents.Count.ToString();
        DoneIncidentCountText.Text = doneCount.ToString();
        OverviewOpenIncidentsCountText.Text = $"{openCount} open incident(s)";

        var selectedNumber = (IncidentsListBox.SelectedItem as Incident)?.IncidentNumber ?? _displayedIncidentNumber;
        var search = searchText.Trim();
        IncidentSeverity? severityFilter = IncidentSeverityFilterComboBox.SelectedIndex switch
        {
            1 => IncidentSeverity.Critical, 2 => IncidentSeverity.High,
            3 => IncidentSeverity.Medium, 4 => IncidentSeverity.Low, _ => null
        };
        var matchingIncidents = _incidents
            // A search should be able to find an incident by ID even when it is already Done.
            // The Open/All buttons control the default queue only when the search box is empty.
            .Where(item => hasSearchText || _showAllIncidents || item.Status == IncidentStatus.Open)
            .Where(item => severityFilter is null || item.Severity == severityFilter.Value)
            .Where(item => string.IsNullOrWhiteSpace(search) ||
                (item.IncidentNumber ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (item.Title ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (item.EndpointUrl ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (item.Details ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (item.Reason ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (item.ResolutionNote ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var visibleIncidents = IncidentSortComboBox.SelectedIndex switch
        {
            1 => matchingIncidents
                .OrderByDescending(item => item.Status == IncidentStatus.Open)
                .ThenByDescending(item => item.LastOccurredAt)
                .ThenBy(item => item.Severity)
                .ToList(),
            2 => matchingIncidents
                .OrderByDescending(item => item.Status == IncidentStatus.Open)
                .ThenByDescending(item => item.OccurrenceCount)
                .ThenBy(item => item.Severity)
                .ThenByDescending(item => item.LastOccurredAt)
                .ToList(),
            _ => matchingIncidents
                .OrderByDescending(item => item.Status == IncidentStatus.Open)
                .ThenBy(item => item.Severity)
                .ThenByDescending(item => item.LastOccurredAt)
                .ToList()
        };

        IncidentsListBox.ItemsSource = visibleIncidents;
        IncidentsListBox.SelectedItem = selectedNumber is null ? null :
            visibleIncidents.FirstOrDefault(item => string.Equals(item.IncidentNumber, selectedNumber, StringComparison.OrdinalIgnoreCase));
        if (IncidentsListBox.SelectedItem is null && visibleIncidents.Count > 0) IncidentsListBox.SelectedIndex = 0;
        IncidentQueueCountText.Text = $"{visibleIncidents.Count} item(s)";
        IncidentFilterDescriptionText.Text = hasSearchText
            ? $"Search results across all statuses • {visibleIncidents.Count} match(es)"
            : _showAllIncidents
                ? $"Showing {visibleIncidents.Count} of {_incidents.Count} incident(s)"
                : $"Showing {visibleIncidents.Count} open incident(s)";
        IncidentsEmptyPanel.Visibility = visibleIncidents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (visibleIncidents.Count == 0)
            IncidentsEmptyText.Text = _incidents.Count == 0
                ? "Failed endpoint checks will appear here automatically."
                : "No incidents match the current search and filters.";
        OpenIncidentsFilterButton.Background = _showAllIncidents
            ? (Brush)FindResource("SecondaryButtonBrush") : (Brush)FindResource("SelectedFilterBrush");
        AllIncidentsFilterButton.Background = _showAllIncidents
            ? (Brush)FindResource("SelectedFilterBrush") : (Brush)FindResource("SecondaryButtonBrush");
        DisplaySelectedIncidentDetails();
        UpdateIncidentActionButtons();
    }

    private void IncidentSearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshIncidentUI();

    private void IncidentSeverityFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshIncidentUI();

    private void IncidentSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshIncidentUI();

    private void SelectedIncidentSeverityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingIncidentDetails) return;
        if (IncidentsListBox.SelectedItem is Incident selected &&
            SelectedIncidentSeverityComboBox.SelectedIndex != (int)selected.Severity)
            IncidentDetailFeedbackText.Text = "Priority changed but not saved. Choose Update priority to persist it.";
        UpdateIncidentActionButtons();
    }

    private void IncidentResolutionTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingIncidentDetails) return;
        if (IncidentsListBox.SelectedItem is Incident selected &&
            !string.Equals(IncidentResolutionTextBox.Text.Trim(), selected.ResolutionNote, StringComparison.Ordinal))
            IncidentDetailFeedbackText.Text = "Unsaved resolution notes — save them before leaving this incident.";
        UpdateIncidentActionButtons();
    }

    private void ClearIncidentSearchButton_Click(object sender, RoutedEventArgs e)
    {
        IncidentSearchTextBox.Clear();
        IncidentSearchTextBox.Focus();
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0 &&
            e.Key == System.Windows.Input.Key.F)
        {
            ShowPage(IncidentsPage, IncidentsNavButton);
            IncidentSearchTextBox.Focus();
            IncidentSearchTextBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (IncidentsPage.Visibility == Visibility.Visible &&
            IncidentSearchTextBox.IsKeyboardFocusWithin &&
            e.Key == System.Windows.Input.Key.Escape &&
            !string.IsNullOrWhiteSpace(IncidentSearchTextBox.Text))
        {
            IncidentSearchTextBox.Clear();
            e.Handled = true;
        }
    }

    private void CopyIncidentIdButton_Click(object sender, RoutedEventArgs e)
    {
        if (IncidentsListBox.SelectedItem is Incident selected)
            CopyIncidentValueToClipboard(selected.IncidentNumber, "Incident ID");
    }

    private void CopyIncidentEndpointButton_Click(object sender, RoutedEventArgs e)
    {
        if (IncidentsListBox.SelectedItem is Incident selected)
            CopyIncidentValueToClipboard(selected.EndpointUrl, "Endpoint URL");
    }

    private void CopyIncidentValueToClipboard(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            IncidentDetailFeedbackText.Text = $"No {label.ToLowerInvariant()} is available to copy.";
            return;
        }

        try
        {
            Clipboard.SetText(value);
            IncidentDetailFeedbackText.Text = $"{label} copied to clipboard.";
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            IncidentDetailFeedbackText.Text = $"Could not copy the {label.ToLowerInvariant()}. Try again.";
        }
    }

    private void DisplaySelectedIncidentDetails()
    {
        if (IncidentsListBox.SelectedItem is not Incident selected)
        {
            IncidentNoSelectionPanel.Visibility = Visibility.Visible;
            IncidentDetailScrollViewer.Visibility = Visibility.Collapsed;
            _displayedIncidentNumber = null;
            return;
        }

        IncidentNoSelectionPanel.Visibility = Visibility.Collapsed;
        IncidentDetailScrollViewer.Visibility = Visibility.Visible;
        _isUpdatingIncidentDetails = true;
        SelectedIncidentTitleText.Text = string.IsNullOrWhiteSpace(selected.Title) ? "Untitled incident" : selected.Title;
        SelectedIncidentIdText.Text = selected.IncidentNumber;
        SelectedIncidentEndpointText.Text = selected.EndpointUrl;
        SelectedIncidentSeverityText.Text = selected.SeverityDisplay.ToUpperInvariant();
        SelectedIncidentSeverityBadge.Background = selected.SeverityBrush;
        SelectedIncidentSeverityComboBox.SelectedIndex = (int)selected.Severity;
        SelectedIncidentStatusText.Text = selected.StatusDisplay.ToUpperInvariant();
        SelectedIncidentStatusBadge.Background = selected.Status == IncidentStatus.Done
            ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("DangerBrush");
        SelectedIncidentFirstSeenText.Text = selected.FirstOccurredAt.ToString("yyyy-MM-dd HH:mm:ss");
        SelectedIncidentLastSeenText.Text = selected.LastOccurredAt.ToString("yyyy-MM-dd HH:mm:ss");
        SelectedIncidentOccurrencesText.Text = selected.OccurrenceCount.ToString();
        SelectedIncidentResolvedAtText.Text = selected.ResolvedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Not resolved";
        SelectedIncidentTechnicalDetailsText.Text = string.IsNullOrWhiteSpace(selected.Details)
            ? "No technical details were recorded for this incident." : selected.Details;
        var lifecycle = new List<string>
        {
            $"FIRST DETECTED   {selected.FirstOccurredAt:yyyy-MM-dd HH:mm:ss}",
            $"LATEST FAILURE   {selected.LastOccurredAt:yyyy-MM-dd HH:mm:ss}",
            $"FAILURE EVENTS   {selected.OccurrenceCount}"
        };
        if (selected.ResolvedAt is DateTime resolvedAt) lifecycle.Add($"LAST RESOLVED    {resolvedAt:yyyy-MM-dd HH:mm:ss}");
        SelectedIncidentTimelineText.Text = string.Join(Environment.NewLine, lifecycle);
        if (!string.Equals(_displayedIncidentNumber, selected.IncidentNumber, StringComparison.OrdinalIgnoreCase))
        {
            IncidentReasonTextBox.Text = selected.Reason ?? string.Empty;
            IncidentResolutionTextBox.Text = selected.ResolutionNote ?? string.Empty;
            IncidentDetailFeedbackText.Text = "Document the confirmed cause or clearly label an unconfirmed hypothesis.";
            _displayedIncidentNumber = selected.IncidentNumber;
        }
        else
        {
            if (!IncidentReasonTextBox.IsKeyboardFocusWithin)
                IncidentReasonTextBox.Text = selected.Reason ?? string.Empty;
            if (!IncidentResolutionTextBox.IsKeyboardFocusWithin)
                IncidentResolutionTextBox.Text = selected.ResolutionNote ?? string.Empty;
        }
        _isUpdatingIncidentDetails = false;
        UpdateIncidentActionButtons();
    }

    private void IncidentReasonTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingIncidentDetails) return;
        if (IncidentsListBox.SelectedItem is Incident selected &&
            !string.Equals(IncidentReasonTextBox.Text.Trim(), selected.Reason, StringComparison.Ordinal))
            IncidentDetailFeedbackText.Text = "Unsaved root-cause notes — save them before leaving this incident.";
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
        var selected = IncidentsListBox.SelectedItem as Incident;
        MarkDoneIncidentButton.IsEnabled = selected is not null && selected.Status == IncidentStatus.Open;
        ReopenIncidentButton.IsEnabled = selected is not null && selected.Status == IncidentStatus.Done;
        SaveIncidentReasonButton.IsEnabled = selected is not null &&
            !string.Equals(IncidentReasonTextBox.Text.Trim(), selected.Reason, StringComparison.Ordinal);
        SaveIncidentResolutionButton.IsEnabled = selected is not null &&
            !string.Equals(IncidentResolutionTextBox.Text.Trim(), selected.ResolutionNote, StringComparison.Ordinal);
        ApplyIncidentSeverityButton.IsEnabled = selected is not null &&
            SelectedIncidentSeverityComboBox.SelectedIndex != (int)selected.Severity;
    }

    private void SetStatus(string message, string brushKey)
    {
        CheckStatusText.Text = message;
        _currentStatusBrushKey = brushKey;
        StatusIndicator.Fill = (Brush)FindResource(brushKey);
    }

    private void OverviewNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(OverviewPage, OverviewNavButton);
    }

    private void ScheduledChecksNavButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateScheduledChecksUI();
        ShowPage(ScheduledChecksPage, ScheduledChecksNavButton);
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

    private sealed class UiPreferences
    {
        public string Theme { get; set; } = "Dark";
    }

    private string UiPreferencesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AlertingSystem",
        "ui-settings.json");

    private void LoadThemePreference()
    {
        var theme = "Dark";
        try
        {
            if (File.Exists(UiPreferencesPath))
            {
                var preferences = System.Text.Json.JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(UiPreferencesPath));
                if (string.Equals(preferences?.Theme, "Light", StringComparison.OrdinalIgnoreCase))
                {
                    theme = "Light";
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Missing or unreadable theme preferences must never prevent monitoring from starting.
            theme = "Dark";
        }

        ApplyTheme(theme);
    }

    private void SaveThemePreference()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UiPreferencesPath)!);
            var preferences = new UiPreferences { Theme = _selectedTheme };
            var json = System.Text.Json.JsonSerializer.Serialize(preferences, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(UiPreferencesPath, json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, $"The theme changed for this session, but the preference could not be saved.\n\n{exception.Message}",
                "Theme preference warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyTheme(string theme)
    {
        _isLightTheme = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
        _selectedTheme = _isLightTheme ? "Light" : "Dark";

        foreach (var entry in ThemeRoleColors)
        {
            var color = _isLightTheme ? entry.Value.Light : entry.Value.Dark;
            Resources[entry.Key] = new SolidColorBrush(ParseThemeColor(color));
        }

        foreach (var darkColor in ThemeLiteralColors)
        {
            var color = darkColor;
            if (_isLightTheme && LightLiteralColorOverrides.TryGetValue(darkColor, out var lightColor))
            {
                color = lightColor;
            }

            var key = "Hex" + darkColor.TrimStart('#') + "Brush";
            Resources[key] = new SolidColorBrush(ParseThemeColor(color));
        }

        StatusIndicator.Fill = (Brush)FindResource(_currentStatusBrushKey);
        OpenIncidentsFilterButton.Background = _showAllIncidents
            ? (Brush)FindResource("SecondaryButtonBrush")
            : (Brush)FindResource("SelectedFilterBrush");
        AllIncidentsFilterButton.Background = _showAllIncidents
            ? (Brush)FindResource("SelectedFilterBrush")
            : (Brush)FindResource("SecondaryButtonBrush");

        ThemeDescriptionText.Text = _isLightTheme
            ? "Light theme is active. Your preference is saved locally on this computer."
            : "Dark theme is active. Your preference is saved locally on this computer.";

        SyncThemeOptionSelection();
        if (_activePage is not null && _activeNavButton is not null)
        {
            UpdateNavigationSelection();
        }
    }

    private static Color ParseThemeColor(string value) =>
        (Color)System.Windows.Media.ColorConverter.ConvertFromString(value)!;

    private void SyncThemeOptionSelection()
    {
        _isUpdatingThemeOptions = true;
        DarkThemeOption.IsChecked = !_isLightTheme;
        LightThemeOption.IsChecked = _isLightTheme;
        _isUpdatingThemeOptions = false;
    }

    private void DarkThemeOption_Checked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingThemeOptions || _selectedTheme == "Dark") return;
        ApplyTheme("Dark");
        SaveThemePreference();
    }

    private void LightThemeOption_Checked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingThemeOptions || _selectedTheme == "Light") return;
        ApplyTheme("Light");
        SaveThemePreference();
    }
    private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(SettingsPage, SettingsNavButton);
    }

    private void ShowPage(Grid selectedPage, Button selectedButton)
    {
        OverviewPage.Visibility = Visibility.Collapsed;
        ScheduledChecksPage.Visibility = Visibility.Collapsed;
        IncidentsPage.Visibility = Visibility.Collapsed;
        AlertHistoryPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        selectedPage.Visibility = Visibility.Visible;

        _activePage = selectedPage;
        _activeNavButton = selectedButton;
        UpdateNavigationSelection();
    }

    private void UpdateNavigationSelection()
    {
        if (_activeNavButton is null)
        {
            return;
        }

        foreach (var button in new[] { OverviewNavButton, ScheduledChecksNavButton, IncidentsNavButton, AlertHistoryNavButton, SettingsNavButton })
        {
            button.Background = Brushes.Transparent;
            button.Foreground = (Brush)FindResource("MutedBrush");
            button.FontWeight = FontWeights.Normal;
        }

        _activeNavButton.Background = (Brush)FindResource("Hex20385FBrush");
        _activeNavButton.Foreground = (Brush)FindResource("HexEAF2FFBrush");
        _activeNavButton.FontWeight = FontWeights.SemiBold;
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
        DisplaySelectedIncidentDetails();
        UpdateIncidentActionButtons();
    }

    private void ApplyIncidentSeverityButton_Click(object sender, RoutedEventArgs e)
    {
        if (IncidentsListBox.SelectedItem is not Incident selected) return;
        var newSeverity = SelectedIncidentSeverityComboBox.SelectedIndex switch
        {
            0 => IncidentSeverity.Critical,
            1 => IncidentSeverity.High,
            2 => IncidentSeverity.Medium,
            3 => IncidentSeverity.Low,
            _ => selected.Severity
        };
        if (newSeverity == selected.Severity)
        {
            IncidentDetailFeedbackText.Text = "Priority is already up to date.";
            UpdateIncidentActionButtons();
            return;
        }

        var previousSeverity = selected.Severity;
        selected.Severity = newSeverity;
        if (PersistIncident(selected))
        {
            IncidentDetailFeedbackText.Text = $"Priority updated to {newSeverity}.";
            RefreshIncidentUI();
        }
        else
        {
            selected.Severity = previousSeverity;
            DisplaySelectedIncidentDetails();
            IncidentDetailFeedbackText.Text = "Priority update failed. Check the MariaDB storage warning and retry.";
        }
    }

    private void SaveIncidentResolutionButton_Click(object sender, RoutedEventArgs e)
    {
        if (IncidentsListBox.SelectedItem is not Incident selected) return;
        var newResolution = IncidentResolutionTextBox.Text.Trim();
        if (string.Equals(newResolution, selected.ResolutionNote, StringComparison.Ordinal))
        {
            IncidentDetailFeedbackText.Text = "There are no unsaved resolution-note changes.";
            UpdateIncidentActionButtons();
            return;
        }

        var previousResolution = selected.ResolutionNote;
        selected.ResolutionNote = newResolution;
        if (PersistIncident(selected))
        {
            IncidentDetailFeedbackText.Text = $"Resolution notes saved to MariaDB at {DateTime.Now:HH:mm:ss}.";
            RefreshIncidentUI();
        }
        else
        {
            selected.ResolutionNote = previousResolution;
            DisplaySelectedIncidentDetails();
            IncidentDetailFeedbackText.Text = "Resolution notes could not be saved. Check the MariaDB storage warning and retry.";
        }
    }

    private void SaveIncidentReasonButton_Click(object sender, RoutedEventArgs e)
    {
        if (IncidentsListBox.SelectedItem is not Incident selected) return;
        var newReason = IncidentReasonTextBox.Text.Trim();
        if (string.Equals(newReason, selected.Reason, StringComparison.Ordinal))
        {
            IncidentDetailFeedbackText.Text = "There are no unsaved reason changes.";
            UpdateIncidentActionButtons();
            return;
        }
        var originalReason = selected.Reason;
        selected.Reason = newReason;
        if (PersistIncident(selected))
        {
            IncidentDetailFeedbackText.Text = $"Reason saved to MariaDB at {DateTime.Now:HH:mm:ss}.";
            RefreshIncidentUI();
        }
        else
        {
            selected.Reason = originalReason;
            IncidentDetailFeedbackText.Text = "Save failed. Check the MariaDB storage warning and retry.";
            DisplaySelectedIncidentDetails();
            UpdateIncidentActionButtons();
        }
    }

    private void MarkDoneIncidentButton_Click(object sender, RoutedEventArgs e)
    {
        if (IncidentsListBox.SelectedItem is not Incident selected || selected.Status == IncidentStatus.Done) return;
        var previousStatus = selected.Status;
        var previousResolvedAt = selected.ResolvedAt;
        var previousResolutionNote = selected.ResolutionNote;
        var draftedResolution = IncidentResolutionTextBox.Text.Trim();
        selected.Status = IncidentStatus.Done;
        selected.ResolvedAt = DateTime.Now;
        var doneNote = $"Marked Done manually at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.";
        selected.ResolutionNote = string.IsNullOrWhiteSpace(draftedResolution)
            ? doneNote : draftedResolution + Environment.NewLine + Environment.NewLine + doneNote;
        if (PersistIncident(selected))
        {
            IncidentDetailFeedbackText.Text = "Incident marked Done and saved.";
            RefreshIncidentUI();
        }
        else
        {
            selected.Status = previousStatus;
            selected.ResolvedAt = previousResolvedAt;
            selected.ResolutionNote = previousResolutionNote;
            DisplaySelectedIncidentDetails();
        }
    }

    private void ReopenIncidentButton_Click(object sender, RoutedEventArgs e)
    {
        if (IncidentsListBox.SelectedItem is not Incident selected || selected.Status != IncidentStatus.Done) return;
        var previousResolvedAt = selected.ResolvedAt;
        selected.Status = IncidentStatus.Open;
        // Keep the previous resolution timestamp and note for audit context.
        if (PersistIncident(selected))
        {
            IncidentDetailFeedbackText.Text = "Incident reopened. Previous resolution context was preserved.";
            RefreshIncidentUI();
        }
        else
        {
            selected.Status = IncidentStatus.Done;
            selected.ResolvedAt = previousResolvedAt;
            DisplaySelectedIncidentDetails();
        }
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
            $"Hide incident {incident.IncidentNumber} from this app?\n\nIts record will remain in MariaDB.\n\n{incident.Title}",
            "Hide incident",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _hiddenItems.HideIncident(incident.IncidentNumber);
        _incidents.Remove(incident);
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
            $"Hide this history entry from the app?\n\nIts record will remain in MariaDB.\n\n{entry.TimestampDisplay}\n{entry.Endpoint}\n{entry.Result}",
            "Hide history entry",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _hiddenItems.HideHistoryEntry(entry.Id);
        _checkHistory.Remove(entry);
        RefreshCheckHistoryUI();
    }
}
