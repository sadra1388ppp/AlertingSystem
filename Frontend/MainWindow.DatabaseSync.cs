using System.IO;
using System.Text.Json;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AlertingSystem.Models;

namespace AlertingSystem;

public partial class MainWindow
{
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
            exception is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ShowStorageWarning(exception);
            return false;
        }
    }

    private async Task<bool> PersistIncidentAsync(Incident incident, bool allowInsert = false)
    {
        try
        {
            return await Task.Run(() => _dataStore.SaveIncident(incident, allowInsert));
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ShowStorageWarning(exception);
            return false;
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
            $"AlertingSystem could not communicate with the backend. The latest change may not have been saved.\n\n{exception.Message}",
            "Backend communication warning", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void DatabaseSyncTimer_Tick(object? sender, EventArgs e)
    {
        await SyncFromDatabaseAsync();
    }

    private async Task SyncFromDatabaseAsync()
    {
        if (_sessionEnding || _databaseSyncRunning)
        {
            return;
        }

        _databaseSyncRunning = true;
        try
        {
            var snapshot = await Task.Run(() =>
            {
                var accountIsActive = _accountStore.IsActiveAccount(_currentUser.Id);
                if (!accountIsActive)
                {
                    return (
                        AccountIsActive: false,
                        Incidents: new List<Incident>(),
                        History: new List<CheckHistoryEntry>());
                }

                var incidents = _dataStore.LoadIncidents();
                var history = _dataStore.LoadCheckHistory();

                return (
                    AccountIsActive: true,
                    Incidents: incidents,
                    History: history);
            });

            // If the user signed out while the query was in progress, do not
            // mutate or notify a closing window.
            if (_sessionEnding)
            {
                return;
            }

            if (!snapshot.AccountIsActive)
            {
                _sessionEnding = true;
                _securityAuditLogger.RecordSessionRevoked(
                    _currentUser,
                    "This account was soft-deleted while a session was still active.");
                MessageBox.Show(
                    this,
                    "This account has been deleted and can no longer access AlertingSystem. Your session will now end. Existing incidents and alert history remain preserved.",
                    "Account access revoked",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(EndSessionAndRequireLogin));
                return;
            }

            var databaseIncidents = snapshot.Incidents;
            var databaseHistory = snapshot.History;
            var visibleIncidents = databaseIncidents
                .Where(incident => !_hiddenItems.IsIncidentHidden(incident.IncidentNumber))
                .ToList();
            var visibleHistory = databaseHistory
                .Where(entry => !_hiddenItems.IsHistoryEntryHidden(entry.Id))
                .ToList();

            var newlyObservedSecurityEvents = visibleHistory
                .Where(entry => entry.Id > 0 &&
                    !_checkHistory.Any(existing => existing.Id == entry.Id) &&
                    IsSecurityAuditEvent(entry))
                .OrderByDescending(entry => entry.Timestamp)
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

            foreach (var securityEvent in newlyObservedSecurityEvents.OrderBy(entry => entry.Timestamp))
            {
                var failedLogin = securityEvent.Result.Equals(
                        "LOGIN FAILED", StringComparison.OrdinalIgnoreCase) ||
                    securityEvent.Result.Equals(
                        "LOGIN ERROR", StringComparison.OrdinalIgnoreCase);
                var accountCreated = securityEvent.Result.Equals(
                    "ACCOUNT CREATED", StringComparison.OrdinalIgnoreCase);
                var accountDeleted = securityEvent.Result.Equals(
                    "ACCOUNT DELETED", StringComparison.OrdinalIgnoreCase);
                var signedOut = securityEvent.Result.Equals(
                    "SIGN OUT", StringComparison.OrdinalIgnoreCase);
                var sessionRevoked = securityEvent.Result.Equals(
                    "SESSION REVOKED", StringComparison.OrdinalIgnoreCase);

                var title = failedLogin
                    ? "SECURITY INCIDENT  /  FAILED SIGN-IN"
                    : accountCreated
                        ? "SECURITY ALERT  /  NEW ACCOUNT"
                        : accountDeleted
                            ? "SECURITY ALERT  /  ACCOUNT DELETED"
                            : sessionRevoked
                                ? "SECURITY ALERT  /  SESSION REVOKED"
                                : signedOut
                                    ? "SECURITY ALERT  /  USER SIGNED OUT"
                                    : "SECURITY ALERT  /  SUCCESSFUL LOGIN";
                var detailLines = securityEvent.Details
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var usefulDetail = failedLogin
                    ? detailLines.FirstOrDefault(line => line.StartsWith("Reason:", StringComparison.OrdinalIgnoreCase))
                        ?? detailLines.FirstOrDefault(line => line.StartsWith("Category:", StringComparison.OrdinalIgnoreCase))
                    : detailLines.FirstOrDefault(line => line.StartsWith("Event:", StringComparison.OrdinalIgnoreCase));
                var message = $"{securityEvent.Endpoint}\n{securityEvent.Timestamp:yyyy-MM-dd HH:mm:ss}\n{usefulDetail ?? securityEvent.Result}";
                var brushKey = failedLogin || accountDeleted || sessionRevoked
                    ? "DangerBrush"
                    : signedOut
                        ? "WarningBrush"
                        : "SuccessBrush";
                QueueSecurityAlertToast(title, message, brushKey);
            }
        }
        catch (Exception exception)
        {
            ShowStorageWarning(exception);
        }
        finally
        {
            _databaseSyncRunning = false;
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

    private static bool IsSecurityAuditEvent(CheckHistoryEntry entry) =>
        entry.Result.Equals("LOGIN SUCCESS", StringComparison.OrdinalIgnoreCase) ||
        entry.Result.Equals("LOGIN FAILED", StringComparison.OrdinalIgnoreCase) ||
        entry.Result.Equals("LOGIN ERROR", StringComparison.OrdinalIgnoreCase) ||
        entry.Result.Equals("ACCOUNT CREATED", StringComparison.OrdinalIgnoreCase) ||
        entry.Result.Equals("SIGN OUT", StringComparison.OrdinalIgnoreCase) ||
        entry.Result.Equals("ACCOUNT DELETED", StringComparison.OrdinalIgnoreCase) ||
        entry.Result.Equals("SESSION REVOKED", StringComparison.OrdinalIgnoreCase);

    private void QueueSecurityAlertToast(string title, string message, string brushKey)
    {
        _pendingSecurityToasts.Enqueue((title, message, brushKey));
        if (!_securityAlertToastTimer.IsEnabled &&
            SecurityAlertToastBorder.Visibility != Visibility.Visible)
        {
            ShowNextSecurityAlertToast();
        }
    }

    private void ShowNextSecurityAlertToast()
    {
        if (_pendingSecurityToasts.Count == 0)
        {
            SecurityAlertToastBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var notification = _pendingSecurityToasts.Dequeue();
        SecurityAlertToastTitle.Text = notification.Title;
        SecurityAlertToastBody.Text = notification.Message;
        var alertBrush = (Brush)FindResource(notification.BrushKey);
        SecurityAlertToastBorder.BorderBrush = alertBrush;
        SecurityAlertToastIndicator.Fill = alertBrush;
        SecurityAlertToastTitle.Foreground = alertBrush;
        SecurityAlertToastBorder.Visibility = Visibility.Visible;
        _securityAlertToastTimer.Stop();
        _securityAlertToastTimer.Start();
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
}
