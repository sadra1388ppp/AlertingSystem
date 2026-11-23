using AlertingSystem.Contracts;
using AlertingSystem.Models;

namespace AlertingSystem.Services;

public sealed class SecurityAuditLogger
{
    public void RecordSuccessfulLogin(AccountUser user, string? context = null) =>
        Send("login-success", user, context);

    public void RecordAccountCreated(AccountUser user) =>
        Send("account-created", user);

    public void RecordSuccessfulLogout(AccountUser user, string? context = null) =>
        Send("logout", user, context);

    public void RecordAccountDeleted(AccountUser user) =>
        Send("account-deleted", user);

    public void RecordSessionRevoked(AccountUser user, string context) =>
        Send("session-revoked", user, context);

    public void RecordFailedLogin(
        string? attemptedUsername,
        string category,
        string reason,
        IncidentSeverity severity = IncidentSeverity.Medium)
    {
        BackendApiClient.Post<OperationResponse>("api/security/events", new SecurityEventRequest
        {
            EventType = "login-failed",
            AttemptedUsername = attemptedUsername,
            Category = category,
            Reason = reason,
            Severity = (int)severity
        });
    }

    public void FlushPendingEvents() =>
        BackendApiClient.Post<OperationResponse>(
            "api/security/events", new SecurityEventRequest { EventType = "flush" });

    private static void Send(string eventType, AccountUser user, string? context = null) =>
        BackendApiClient.Post<OperationResponse>("api/security/events", new SecurityEventRequest
        {
            EventType = eventType,
            UserId = user.Id,
            Context = context
        });
}
