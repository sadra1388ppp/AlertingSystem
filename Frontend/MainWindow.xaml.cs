using System.Diagnostics;
using System.Data.Common;
using System.IO;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AlertingSystem.Models;
using AlertingSystem.Services;
using Microsoft.EntityFrameworkCore;
using System.Windows.Threading;

namespace AlertingSystem;

public partial class MainWindow : Window
{

    private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeWindowButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly MariaDbDataStore _dataStore = new();
    private readonly AccountStore _accountStore = new();
    private readonly SecurityAuditLogger _securityAuditLogger = new();
    private readonly AccountUser _currentUser;
    private bool _sessionEnding;
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
        // Poll less often; the full database read now runs away from the UI thread.
        Interval = TimeSpan.FromSeconds(10)
    };
    private bool _databaseSyncRunning;
    private readonly DispatcherTimer _securityAlertToastTimer = new()
    {
        Interval = TimeSpan.FromSeconds(8)
    };
    private readonly Queue<(string Title, string Message, string BrushKey)> _pendingSecurityToasts = new();
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
        ["AppBackgroundBrush"] = ("#07151D", "#F3F8FA"),
        ["SidebarBrush"] = ("#0B1C25", "#E9F2F5"),
        ["CardBrush"] = ("#102630", "#FFFFFF"),
        ["BorderBrush"] = ("#26434D", "#D3E2E8"),
        ["TextBrush"] = ("#E8F6F8", "#172D37"),
        ["MutedBrush"] = ("#91ADB6", "#617A84"),
        ["SuccessBrush"] = ("#47D7AA", "#168567"),
        ["CriticalBrush"] = ("#FF7B85", "#BF3A4A"),
        ["WarningBrush"] = ("#F0C26D", "#936400"),
        ["DangerBrush"] = ("#FF6E82", "#BF394D"),
        ["NeutralBrush"] = ("#55CDD6", "#127F90"),
        ["SecondaryButtonBrush"] = ("#16333F", "#E4F1F4"),
        ["SelectedFilterBrush"] = ("#0EBAC8", "#0C9EA9"),
        ["HistorySelectionBrush"] = ("#104451", "#C5F0F2")
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

    private static readonly Dictionary<string, string> DarkLiteralColorOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#0A1020"] = "#07151D",
        ["#0B1120"] = "#0B1C25",
        ["#0B1425"] = "#0A1B24",
        ["#101A2B"] = "#102630",
        ["#101B2F"] = "#142E39",
        ["#14233A"] = "#173642",
        ["#162743"] = "#1B414D",
        ["#18253B"] = "#16333F",
        ["#1A2942"] = "#183743",
        ["#20385F"] = "#104451",
        ["#294568"] = "#177080",
        ["#302713"] = "#3A3421",
        ["#3B82F6"] = "#2AC9D5",
        ["#477DFA"] = "#0EBAC8",
        ["#5C8CFF"] = "#46D6DE",
        ["#64748B"] = "#617C8A",
        ["#71829C"] = "#7897A3",
        ["#75602A"] = "#6D5A2D",
        ["#91A2BB"] = "#91ADB6",
        ["#D8E4F5"] = "#CFE9EE",
        ["#EAF2FF"] = "#EAF9FC",
        ["#EEF3FA"] = "#E8F6F8",
        ["#FDE68A"] = "#F7DB91"
    };

    private static readonly Dictionary<string, string> LightLiteralColorOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#0A1020"] = "#F3F8FA",
        ["#0B1120"] = "#1A3642",
        ["#0B1425"] = "#F8FBFC",
        ["#101A2B"] = "#FFFFFF",
        ["#101B2F"] = "#FFFFFF",
        ["#14233A"] = "#E5F1F4",
        ["#162743"] = "#DDEEF2",
        ["#18253B"] = "#E4F1F4",
        ["#1A2942"] = "#DCEBF0",
        ["#20385F"] = "#C5F0F2",
        ["#294568"] = "#95DDE3",
        ["#302713"] = "#FFF3D2",
        ["#3B82F6"] = "#0E8695",
        ["#477DFA"] = "#0C9EA9",
        ["#5C8CFF"] = "#0C8C9A",
        ["#64748B"] = "#6A818B",
        ["#71829C"] = "#758D97",
        ["#75602A"] = "#D6C184",
        ["#91A2BB"] = "#617A84",
        ["#D8E4F5"] = "#294A56",
        ["#EAF2FF"] = "#254C58",
        ["#EEF3FA"] = "#172D37",
        ["#FDE68A"] = "#805900"
    };

    public MainWindow(AccountUser currentUser)
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        InitializeComponent();
        AccountInitialText.Text = _currentUser.DisplayName[..1].ToUpperInvariant();
        AccountDisplayNameText.Text = _currentUser.DisplayName;
        TextDirectionHelper.Apply(AccountDisplayNameText, _currentUser.DisplayName);
        AccountUsernameText.Text = "@" + _currentUser.Username;
        TextDirectionHelper.Apply(AccountUsernameText, _currentUser.Username);
        AccountIdText.Text = _currentUser.Id.ToString();
        AccountCreatedText.Text = _currentUser.CreatedAt.ToString("yyyy-MM-dd");
        SidebarCurrentUserText.Text = _currentUser.DisplayName;
        TextDirectionHelper.Apply(SidebarCurrentUserText, _currentUser.DisplayName);
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        LoadThemePreference();
        ShowPage(OverviewPage, OverviewNavButton);

        AttachContextMenuHandler(
            FindResource("IncidentListBoxItemStyle") as Style,
            DeleteIncidentMenuItem_Click);
        AttachContextMenuHandler(
            HistoryEntriesList.ItemContainerStyle,
            DeleteHistoryEntryMenuItem_Click);

        var databaseIncidents = _dataStore.LoadIncidents();
        var databaseHistory = _dataStore.LoadCheckHistory();

        _hiddenItems.Reconcile(
            databaseIncidents,
            _dataStore.LoadCheckHistoryIds(_hiddenItems.GetHiddenHistoryIds()));

        var migratedIncidentData = false;
        foreach (var incident in databaseIncidents)
        {
            if (incident.Status != IncidentStatus.Open && incident.Status != IncidentStatus.Done)
            {
                incident.Status = IncidentStatus.Open;
                migratedIncidentData = true;
            }

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

        IncidentSeverityFilterComboBox.SelectionChanged += IncidentSeverityFilterComboBox_SelectionChanged;
        IncidentSortComboBox.SelectionChanged += IncidentSortComboBox_SelectionChanged;
        SelectedIncidentSeverityComboBox.SelectionChanged += SelectedIncidentSeverityComboBox_SelectionChanged;

        IncidentSeverityFilterComboBox.DropDownOpened += IncidentComboBox_DropDownOpened;
        IncidentSortComboBox.DropDownOpened += IncidentComboBox_DropDownOpened;
        SelectedIncidentSeverityComboBox.DropDownOpened += IncidentComboBox_DropDownOpened;

        IncidentReasonTextBox.TextChanged += IncidentReasonTextBox_TextChanged;
        IncidentResolutionTextBox.TextChanged += IncidentResolutionTextBox_TextChanged;

        RefreshIncidentUI();
        RefreshCheckHistoryUI();

        _databaseSyncTimer.Tick += DatabaseSyncTimer_Tick;
        _databaseSyncTimer.Start();
        _securityAlertToastTimer.Tick += (_, _) =>
        {
            _securityAlertToastTimer.Stop();
            if (_pendingSecurityToasts.Count > 0)
            {
                ShowNextSecurityAlertToast();
            }
            else
            {
                SecurityAlertToastBorder.Visibility = Visibility.Collapsed;
            }
        };
        _scheduledCheckTimer.Tick += ScheduledCheckTimer_Tick;
        InitializeScheduledChecks();
        Closed += (_, _) =>
        {
            _databaseSyncTimer.Stop();
            _scheduledCheckTimer.Stop();
            _securityAlertToastTimer.Stop();
        };
    }

    private void IncidentComboBox_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox comboBox)
        {
            return;
        }

        ApplyIncidentDropdownPopupTheme(comboBox);

        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => ApplyIncidentDropdownPopupTheme(comboBox)));
    }

    private void ApplyIncidentDropdownPopupTheme(ComboBox comboBox)
    {
        if (!comboBox.IsDropDownOpen)
        {
            return;
        }

        if (comboBox.Template.FindName("PART_Popup", comboBox)
            is not System.Windows.Controls.Primitives.Popup popup)
        {
            return;
        }

        var surfaceBrush = (Brush)FindResource("CardBrush");
        var outlineBrush = (Brush)FindResource("BorderBrush");

        if (popup.Child is Panel popupPanel)
        {
            popupPanel.Background = surfaceBrush;
        }

        if (popup.Child is not DependencyObject popupRoot)
        {
            return;
        }

        var surface = FindFirstVisualBorder(popupRoot);
        if (surface is null)
        {
            return;
        }

        surface.Background = surfaceBrush;
        surface.BorderBrush = outlineBrush;
        surface.BorderThickness = new Thickness(1);
        surface.CornerRadius = new CornerRadius(8);
    }

    private static Border? FindFirstVisualBorder(DependencyObject parent)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Border border)
            {
                return border;
            }

            var nestedBorder = FindFirstVisualBorder(child);
            if (nestedBorder is not null)
            {
                return nestedBorder;
            }
        }

        return null;
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


}
