using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AlertingSystem.Models;

namespace AlertingSystem;

public partial class MainWindow
{
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

        var selectedNumber = (IncidentsListBox.SelectedItem as Incident)?.IncidentNumber ?? _displayedIncidentNumber;
        var search = searchText.Trim();
        IncidentSeverity? severityFilter = IncidentSeverityFilterComboBox.SelectedIndex switch
        {
            1 => IncidentSeverity.Critical, 2 => IncidentSeverity.High,
            3 => IncidentSeverity.Medium, 4 => IncidentSeverity.Low, _ => null
        };
        var matchingIncidents = _incidents
            .Where(item => hasSearchText || _showAllIncidents || item.Status == IncidentStatus.Open)
            .Where(item => hasSearchText || severityFilter is null || item.Severity == severityFilter.Value)
            .Where(item => MatchesIncidentSearch(item, search))
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

    private static bool MatchesIncidentSearch(Incident incident, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var incidentNumber = incident.IncidentNumber ?? string.Empty;
        if (incidentNumber.Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedSearch = NormalizeSearchToken(search);
        if (normalizedSearch.Length > 0 &&
            NormalizeSearchToken(incidentNumber).Contains(normalizedSearch, StringComparison.Ordinal))
        {
            return true;
        }

        return new[]
        {
            incident.Title,
            incident.EndpointUrl,
            incident.Details,
            incident.Reason,
            incident.ResolutionNote
        }.Any(value => !string.IsNullOrEmpty(value) &&
            value.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeSearchToken(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private void HistoryList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBox historyList)
        {
            return;
        }

        // Apply the themed template after WPF creates the ListBox's internal ScrollViewer and ScrollBars.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (!historyList.IsLoaded)
                {
                    return;
                }

                ApplyHistoryScrollbarStyle(historyList);
            }));
    }

    private void ApplyHistoryScrollbarStyle(DependencyObject parent)
    {
        if (parent is FrameworkElement element)
        {
            element.ApplyTemplate();
        }

        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is System.Windows.Controls.Primitives.ScrollBar scrollBar)
            {
                scrollBar.Style = (Style)FindResource("ThemedEndpointHistoryScrollBarStyle");
                scrollBar.ApplyTemplate();
                continue;
            }

            ApplyHistoryScrollbarStyle(child);
        }
    }
    private void RefreshCheckHistoryUI()
    {
        HistoryList.Items.Clear();
        foreach (var entry in _checkHistory.Take(10))
        {
            HistoryList.Items.Add(entry);
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
