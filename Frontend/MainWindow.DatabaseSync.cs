using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AlertingSystem.Models;
using AlertingSystem.Services;
using Microsoft.EntityFrameworkCore;
namespace AlertingSystem;

public partial class MainWindow
{
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

    private async Task<Incident> UpsertIncidentAsync(
        Uri uri,
        string title,
        IncidentSeverity severity,
        string details)
    {
        var endpointKey = NormalizeEndpoint(uri);
        var incident = _incidents.FirstOrDefault(item =>
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

            if ((int)severity < (int)incident.Severity)
            {
                incident.Severity = severity;
            }
        }

        // The MariaDB write no longer blocks the dispatcher during endpoint checks.
        await PersistIncidentAsync(incident, allowInsert: created);
        RefreshIncidentUI();
        return incident;
    }

    private async Task<string?> MarkMatchingIncidentDoneAsync(Uri uri, string recoveryDetails)
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
        await PersistIncidentAsync(incident);
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

    private async Task RecordCheckAsync(
        string endpoint,
        string result,
        string details,
        long? responseTimeMs)
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

        await PersistCheckHistoryAsync(entry);
        RefreshCheckHistoryUI();
    }

    private async Task PersistCheckHistoryAsync(CheckHistoryEntry entry)
    {
        try
        {
            await Task.Run(() => _dataStore.SaveCheckHistoryEntry(entry));
        }
        catch (Exception exception) when (
            exception is DbException or DbUpdateException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ShowStorageWarning(exception);
        }
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

    private async Task<bool> PersistIncidentAsync(Incident incident, bool allowInsert = false)
    {
        try
        {
            return await Task.Run(() => _dataStore.SaveIncident(incident, allowInsert));
        }
        catch (Exception exception) when (
            exception is DbException or DbUpdateException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
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
            $"AlertingSystem could not save monitoring data to MariaDB. The latest change may not survive an application restart.\n\n{exception.Message}",
            "MariaDB storage warning", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            // Only hidden history IDs need reconciliation. Snapshot this tiny set
            // on the UI thread before querying MariaDB in the background.
            var hiddenHistoryIds = _hiddenItems.GetHiddenHistoryIds();

            var snapshot = await Task.Run(() =>
            {
                var accountIsActive = _accountStore.IsActiveAccount(_currentUser.Id);
                if (!accountIsActive)
                {
                    return (
                        AccountIsActive: false,
                        Incidents: new List<Incident>(),
                        History: new List<CheckHistoryEntry>(),
                        ExistingHiddenHistoryIds: new HashSet<long>());
                }

                var incidents = _dataStore.LoadIncidents();
                var history = _dataStore.LoadCheckHistory();
                var existingHiddenHistoryIds = _dataStore.LoadCheckHistoryIds(hiddenHistoryIds);

                return (
                    AccountIsActive: true,
                    Incidents: incidents,
                    History: history,
                    ExistingHiddenHistoryIds: existingHiddenHistoryIds);
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
            _hiddenItems.Reconcile(databaseIncidents, snapshot.ExistingHiddenHistoryIds);

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
