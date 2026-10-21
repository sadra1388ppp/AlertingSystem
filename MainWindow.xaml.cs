using System.Diagnostics;
using System.Data.Common;
using System.IO;
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
    private readonly DispatcherTimer _databaseSyncTimer = new()
    {
        Interval = TimeSpan.FromSeconds(3)
    };
    private int _checksRun;
    private bool _showAllIncidents;
    private bool _storageWarningShown;
    private bool _isLightTheme;
    private bool _isUpdatingThemeOptions;
    private string _selectedTheme = "Dark";
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

        var migratedLegacyStatuses = false;
        foreach (var incident in databaseIncidents)
        {
            if (incident.Status != IncidentStatus.Open && incident.Status != IncidentStatus.Done)
            {
                incident.Status = IncidentStatus.Open;
                migratedLegacyStatuses = true;
            }
        }

        if (migratedLegacyStatuses)
        {
            _dataStore.SaveIncidents(databaseIncidents);
        }

        _incidents = databaseIncidents
            .Where(incident => !_hiddenItems.IsIncidentHidden(incident.IncidentNumber))
            .ToList();

        _checkHistory = databaseHistory
            .Where(entry => !_hiddenItems.IsHistoryEntryHidden(entry.Id))
            .ToList();

        RefreshIncidentUI();
        RefreshCheckHistoryUI();

        _databaseSyncTimer.Tick += (_, _) => SyncFromDatabase();
        _databaseSyncTimer.Start();
        Closed += (_, _) => _databaseSyncTimer.Stop();
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

    private void PersistIncident(Incident incident, bool allowInsert = false)
    {
        try
        {
            _dataStore.SaveIncident(incident, allowInsert);
        }
        catch (Exception exception) when (
            exception is DbException or DbUpdateException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ShowStorageWarning(exception);
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

        foreach (var button in new[] { OverviewNavButton, IncidentsNavButton, AlertHistoryNavButton, SettingsNavButton })
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
        PersistIncident(selected);
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
