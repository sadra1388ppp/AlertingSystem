using AlertingSystem.Backend.Models;
using AlertingSystem.Backend.Services;
using AlertingSystem.Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(
    Environment.GetEnvironmentVariable("ALERTINGSYSTEM_API_LISTEN_URL")
    ?? "http://127.0.0.1:5078");

builder.Services.AddSingleton<AccountStore>();
builder.Services.AddSingleton<MariaDbDataStore>();
builder.Services.AddSingleton<SecurityAuditLogger>();
builder.Services.AddSingleton<EndpointMonitoringService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "AlertingSystem.Backend" }));

app.MapGet("/api/accounts/exists", (AccountStore store) =>
    Results.Ok(store.HasAccounts()));

app.MapPost("/api/accounts/authenticate", (LoginRequest request, AccountStore store) =>
{
    var success = store.TryAuthenticate(request.Username, request.Password, out var user);
    return Results.Ok(new AuthenticationResponse
    {
        Success = success,
        User = user is null ? null : ToDto(user)
    });
});

app.MapPost("/api/accounts", (CreateAccountRequest request, AccountStore store) =>
{
    var success = store.TryCreateAccount(
        request.DisplayName,
        request.Username,
        request.Password,
        out var user,
        out var error);

    return Results.Ok(new CreateAccountResponse
    {
        Success = success,
        User = user is null ? null : ToDto(user),
        Error = error
    });
});

app.MapGet("/api/accounts", (AccountStore store) =>
    Results.Ok(store.GetAllAccounts().Select(ToDto).ToList()));

app.MapGet("/api/accounts/{id:int}/active", (int id, AccountStore store) =>
    Results.Ok(store.IsActiveAccount(id)));

app.MapPost("/api/accounts/{id:int}/delete", (int id, DeleteAccountRequest request, AccountStore store) =>
{
    var success = store.TrySoftDeleteAccount(id, request.Password, out var error);
    return Results.Ok(new OperationResponse { Success = success, Error = error });
});

app.MapGet("/api/incidents", (MariaDbDataStore store) =>
    Results.Ok(store.LoadIncidents().Select(ToDto).ToList()));

app.MapGet("/api/history", (MariaDbDataStore store) =>
    Results.Ok(store.LoadCheckHistory().Select(ToDto).ToList()));

app.MapPut("/api/incidents", (List<IncidentDto> items, MariaDbDataStore store) =>
{
    store.SaveIncidents(items.Select(FromDto));
    return Results.Ok(new OperationResponse { Success = true });
});

app.MapPut("/api/incidents/item", (IncidentSaveRequest request, MariaDbDataStore store) =>
{
    var success = store.SaveIncident(FromDto(request.Incident), request.AllowInsert);
    return Results.Ok(new OperationResponse { Success = success });
});

app.MapPut("/api/history/item", (CheckHistoryEntryDto item, MariaDbDataStore store) =>
{
    var entry = FromDto(item);
    store.SaveCheckHistoryEntry(entry);
    return Results.Ok(new OperationResponse { Success = true, Id = entry.Id });
});

app.MapPost("/api/security/events", (SecurityEventRequest request, SecurityAuditLogger logger) =>
{
    switch (request.EventType)
    {
        case "flush":
            logger.FlushPendingEvents();
            break;
        case "login-success":
            logger.RecordSuccessfulLogin(RequireUser(request.User), request.Context);
            break;
        case "account-created":
            logger.RecordAccountCreated(RequireUser(request.User));
            break;
        case "logout":
            logger.RecordSuccessfulLogout(RequireUser(request.User), request.Context);
            break;
        case "account-deleted":
            logger.RecordAccountDeleted(RequireUser(request.User));
            break;
        case "session-revoked":
            logger.RecordSessionRevoked(RequireUser(request.User), request.Context ?? string.Empty);
            break;
        case "login-failed":
            logger.RecordFailedLogin(
                request.AttemptedUsername,
                request.Category ?? "Sign-in failed",
                request.Reason ?? "No additional information was supplied.",
                Enum.IsDefined(typeof(IncidentSeverity), request.Severity)
                    ? (IncidentSeverity)request.Severity
                    : IncidentSeverity.Medium);
            break;
        default:
            return Results.BadRequest(new OperationResponse
            {
                Success = false,
                Error = "Unknown security event type."
            });
    }

    return Results.Ok(new OperationResponse { Success = true });
});

app.MapPost("/api/monitoring/check", async (
    MonitoringCheckRequest request,
    EndpointMonitoringService monitoring,
    CancellationToken cancellationToken) =>
{
    var result = await monitoring.CheckAsync(request.Address, cancellationToken);
    return Results.Ok(result);
});

app.Run();

static AccountUser RequireUser(AccountDto? user)
{
    if (user is null)
        throw new ArgumentException("The account information is required.");

    return new AccountUser
    {
        Id = user.Id,
        DisplayName = user.DisplayName,
        Username = user.Username,
        CreatedAt = user.CreatedAt,
        IsDeleted = user.IsDeleted,
        DeletedAt = user.DeletedAt
    };
}

static AccountDto ToDto(AccountUser user) => new()
{
    Id = user.Id,
    DisplayName = user.DisplayName,
    Username = user.Username,
    CreatedAt = user.CreatedAt,
    IsDeleted = user.IsDeleted,
    DeletedAt = user.DeletedAt
};

static IncidentDto ToDto(Incident item) => new()
{
    IncidentNumber = item.IncidentNumber,
    EndpointUrl = item.EndpointUrl,
    Title = item.Title,
    Details = item.Details,
    ResolutionNote = item.ResolutionNote,
    Reason = item.Reason,
    Severity = (int)item.Severity,
    Status = (int)item.Status,
    FirstOccurredAt = item.FirstOccurredAt,
    LastOccurredAt = item.LastOccurredAt,
    ResolvedAt = item.ResolvedAt,
    OccurrenceCount = item.OccurrenceCount
};

static Incident FromDto(IncidentDto item) => new()
{
    IncidentNumber = item.IncidentNumber,
    EndpointUrl = item.EndpointUrl,
    Title = item.Title,
    Details = item.Details,
    ResolutionNote = item.ResolutionNote,
    Reason = item.Reason,
    Severity = Enum.IsDefined(typeof(IncidentSeverity), item.Severity)
        ? (IncidentSeverity)item.Severity : IncidentSeverity.High,
    Status = Enum.IsDefined(typeof(IncidentStatus), item.Status)
        ? (IncidentStatus)item.Status : IncidentStatus.Open,
    FirstOccurredAt = item.FirstOccurredAt,
    LastOccurredAt = item.LastOccurredAt,
    ResolvedAt = item.ResolvedAt,
    OccurrenceCount = Math.Max(1, item.OccurrenceCount)
};

static CheckHistoryEntryDto ToDto(CheckHistoryEntry item) => new()
{
    Id = item.Id,
    Timestamp = item.Timestamp,
    Endpoint = item.Endpoint,
    Result = item.Result,
    Details = item.Details,
    ResponseTimeMs = item.ResponseTimeMs
};

static CheckHistoryEntry FromDto(CheckHistoryEntryDto item) => new()
{
    Id = item.Id,
    Timestamp = item.Timestamp,
    Endpoint = item.Endpoint,
    Result = item.Result,
    Details = item.Details,
    ResponseTimeMs = item.ResponseTimeMs
};
