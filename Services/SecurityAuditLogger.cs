using System.IO;
using System.Text.Json;
using AlertingSystem.Models;

namespace AlertingSystem.Services;

public sealed class SecurityAuditLogger
{
    private static readonly object PendingFileLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _pendingFilePath;
    private MariaDbDataStore? _dataStore;

    public SecurityAuditLogger()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlertingSystem");
        Directory.CreateDirectory(directory);
        _pendingFilePath = Path.Combine(directory, "security-audit-pending.json");
    }

    public void RecordSuccessfulLogin(AccountUser user, string? context = null)
    {
        var now = DateTime.Now;
        var details = BuildBaseDetails(now, user.Username) +
            Environment.NewLine + "Event: Login succeeded." +
            (string.IsNullOrWhiteSpace(context)
                ? string.Empty
                : Environment.NewLine + "Context: " + context);

        Record(new PendingSecurityAuditEvent
        {
            HistoryEntry = new CheckHistoryEntry
            {
                Timestamp = now,
                Endpoint = $"Authentication / @{user.Username}",
                Result = "LOGIN SUCCESS",
                Details = details
            }
        });
    }

    public void RecordAccountCreated(AccountUser user)
    {
        var now = DateTime.Now;
        var details = BuildBaseDetails(now, user.Username) +
            Environment.NewLine + "Event: Account registration succeeded." +
            Environment.NewLine + $"Account ID: {user.Id}";

        Record(new PendingSecurityAuditEvent
        {
            HistoryEntry = new CheckHistoryEntry
            {
                Timestamp = now,
                Endpoint = $"Authentication / @{user.Username}",
                Result = "ACCOUNT CREATED",
                Details = details
            }
        });
    }

    public void RecordSuccessfulLogout(AccountUser user, string? context = null)
    {
        var now = DateTime.Now;
        var details = BuildBaseDetails(now, user.Username) +
            Environment.NewLine + "Event: Sign-out completed." +
            (string.IsNullOrWhiteSpace(context)
                ? string.Empty
                : Environment.NewLine + "Context: " + context);

        Record(new PendingSecurityAuditEvent
        {
            HistoryEntry = new CheckHistoryEntry
            {
                Timestamp = now,
                Endpoint = $"Authentication / @{user.Username}",
                Result = "SIGN OUT",
                Details = details
            }
        });
    }

    public void RecordAccountDeleted(AccountUser user)
    {
        var now = DateTime.Now;
        var details = BuildBaseDetails(now, user.Username) +
            Environment.NewLine + "Event: Account soft-deleted by its owner." +
            Environment.NewLine + $"Account ID: {user.Id}" +
            Environment.NewLine + "Database retention: the AppUsers row remains and is marked IsDeleted = 1 with DeletedAt set." +
            Environment.NewLine + "Related incidents and alert history were not deleted.";

        Record(new PendingSecurityAuditEvent
        {
            HistoryEntry = new CheckHistoryEntry
            {
                Timestamp = now,
                Endpoint = $"Authentication / @{user.Username}",
                Result = "ACCOUNT DELETED",
                Details = details
            }
        });
    }

    public void RecordSessionRevoked(AccountUser user, string context)
    {
        var now = DateTime.Now;
        var details = BuildBaseDetails(now, user.Username) +
            Environment.NewLine + "Event: Session access was revoked." +
            Environment.NewLine + "Reason: " + context;

        Record(new PendingSecurityAuditEvent
        {
            HistoryEntry = new CheckHistoryEntry
            {
                Timestamp = now,
                Endpoint = $"Authentication / @{user.Username}",
                Result = "SESSION REVOKED",
                Details = details
            }
        });
    }

    public void RecordFailedLogin(
        string? attemptedUsername,
        string category,
        string reason,
        IncidentSeverity severity = IncidentSeverity.Medium)
    {
        var now = DateTime.Now;
        var username = SanitizeUsername(attemptedUsername);
        var label = username.Length == 0 ? "(empty username)" : "@" + username;
        var fullReason = $"{category}: {reason} Attempted username: {label}. " +
            "The submitted password was not recorded.";
        var details = BuildBaseDetails(now, username) +
            Environment.NewLine + "Event: Sign-in failed." +
            Environment.NewLine + "Category: " + category +
            Environment.NewLine + "Reason: " + reason +
            Environment.NewLine + "Submitted password: not recorded.";

        var incident = new Incident
        {
            IncidentNumber = $"INC-SEC-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            EndpointUrl = $"Authentication / {label}",
            Title = $"Failed sign-in attempt for {label}",
            Details = details,
            Reason = fullReason,
            ResolutionNote = string.Empty,
            Severity = severity,
            Status = IncidentStatus.Open,
            FirstOccurredAt = now,
            LastOccurredAt = now,
            OccurrenceCount = 1
        };

        var historyEntry = new CheckHistoryEntry
        {
            Timestamp = now,
            Endpoint = $"Authentication / {label}",
            Result = "LOGIN FAILED",
            Details = details
        };

        Record(new PendingSecurityAuditEvent
        {
            Incident = incident,
            HistoryEntry = historyEntry
        });
    }

    public void FlushPendingEvents()
    {
        lock (PendingFileLock)
        {
            if (!File.Exists(_pendingFilePath))
            {
                return;
            }

            List<PendingSecurityAuditEvent> pending;
            try
            {
                pending = JsonSerializer.Deserialize<List<PendingSecurityAuditEvent>>(
                    File.ReadAllText(_pendingFilePath), JsonOptions) ?? [];
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return;
            }

            var remaining = new List<PendingSecurityAuditEvent>();
            foreach (var item in pending)
            {
                try
                {
                    Persist(GetDataStore(), item);
                }
                catch (Exception)
                {
                    remaining.Add(item);
                }
            }

            try
            {
                if (remaining.Count == 0)
                {
                    File.Delete(_pendingFilePath);
                }
                else
                {
                    File.WriteAllText(
                        _pendingFilePath,
                        JsonSerializer.Serialize(remaining, JsonOptions));
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }
    }

    private void Record(PendingSecurityAuditEvent item)
    {
        try
        {
            Persist(GetDataStore(), item);
        }
        catch (Exception)
        {
            QueuePending(item);
            return;
        }

        FlushPendingEvents();
    }

    private MariaDbDataStore GetDataStore() => _dataStore ??= new MariaDbDataStore();

    private static void Persist(MariaDbDataStore dataStore, PendingSecurityAuditEvent item)
    {
        if (item.Incident is not null &&
            !dataStore.SaveIncident(item.Incident, allowInsert: true))
        {
            throw new InvalidOperationException("The security incident could not be persisted.");
        }

        if (item.HistoryEntry is not null)
        {
            dataStore.SaveCheckHistoryEntry(item.HistoryEntry);
        }
    }

    private void QueuePending(PendingSecurityAuditEvent item)
    {
        lock (PendingFileLock)
        {
            try
            {
                var pending = File.Exists(_pendingFilePath)
                    ? JsonSerializer.Deserialize<List<PendingSecurityAuditEvent>>(
                        File.ReadAllText(_pendingFilePath), JsonOptions) ?? []
                    : [];
                pending.Add(item);
                File.WriteAllText(_pendingFilePath, JsonSerializer.Serialize(pending, JsonOptions));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }
    }

    private static string BuildBaseDetails(DateTime timestamp, string username) =>
        "Security event recorded by AlertingSystem." +
        Environment.NewLine + $"Time (local): {timestamp:yyyy-MM-dd HH:mm:ss}" +
        Environment.NewLine + $"Attempted account: {(string.IsNullOrWhiteSpace(username) ? "(empty username)" : "@" + username)}" +
        Environment.NewLine + $"Client machine: {Environment.MachineName}" +
        Environment.NewLine + "Client IP: not available in this local desktop sign-in event.";

    private static string SanitizeUsername(string? username)
    {
        var value = (username ?? string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();
        return value.Length > 100 ? value[..100] : value;
    }

    private sealed class PendingSecurityAuditEvent
    {
        public Incident? Incident { get; set; }
        public CheckHistoryEntry? HistoryEntry { get; set; }
    }
}
