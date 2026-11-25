using AlertingSystem.Backend.Models;
using AlertingSystem.Backend.Services;
using AlertingSystem.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

var listenUrls = (Environment.GetEnvironmentVariable("ALERTINGSYSTEM_API_LISTEN_URL")
    ?? "http://127.0.0.1:5078")
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

if (listenUrls.Length == 0 || listenUrls.Any(url =>
        !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
        !uri.IsLoopback ||
        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
{
    throw new InvalidOperationException(
        "AlertingSystem API must listen on a loopback HTTP/HTTPS address. " +
        "Remote access requires a separately secured deployment.");
}

builder.WebHost.UseUrls(listenUrls);
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "AlertingSystem API",
        Version = "v1",
        Description = "Local HTTP API for account management, incidents, alert history, security auditing, and endpoint monitoring."
    });
});

builder.Services.AddSingleton<AlertingDbContextFactory>();
builder.Services.AddSingleton<AccountStore>();
builder.Services.AddSingleton<MariaDbDataStore>();
builder.Services.AddSingleton<SecurityAuditLogger>();
builder.Services.AddSingleton<EndpointMonitoringService>();

var app = builder.Build();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "AlertingSystem API v1");
        options.RoutePrefix = "swagger";
        options.DocumentTitle = "AlertingSystem API";
    });
}

app.MapGet("/health", () =>
    Results.Ok(new { status = "ok", service = "AlertingSystem.Backend" }))
    .WithTags("Health")
    .WithName("GetHealth")
    .WithSummary("Check backend health")
    .WithDescription("Returns a basic status response indicating that the backend process is responding.");

app.MapGet("/health/live", () =>
    Results.Ok(new { status = "ok", service = "AlertingSystem.Backend" }))
    .WithTags("Health")
    .WithName("GetLiveness")
    .WithSummary("Check backend liveness")
    .WithDescription("Checks that the backend process is running. It does not check database connectivity.");

app.MapGet("/health/ready", (IServiceProvider services, ILogger<AlertingDbContextFactory> logger) =>
{
    try
    {
        using var db = services.GetRequiredService<AlertingDbContextFactory>().CreateContext();
        return db.Database.CanConnect()
            ? Results.Ok(new { status = "ready", database = "available" })
            : Results.Problem(
                title: "Backend is not ready",
                detail: "The configured database is unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception exception)
    {
        logger.LogWarning(exception, "Backend readiness check failed.");
        return Results.Problem(
            title: "Backend is not ready",
            detail: "The configured database could not be initialized or reached.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
    .WithTags("Health")
    .WithName("GetReadiness")
    .WithSummary("Check backend and database readiness")
    .WithDescription("Attempts to connect to the configured MariaDB database. Returns 503 when the database cannot be reached or initialized.")
    .Produces(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/api/accounts/exists", (AccountStore store) =>
    Results.Ok(store.HasAccounts()))
    .WithTags("Accounts")
    .WithName("GetAccountsExistence")
    .WithSummary("Check whether any accounts exist")
    .WithDescription("Returns true when at least one account exists in the account store.")
    .Produces<bool>(StatusCodes.Status200OK);

app.MapPost("/api/accounts/authenticate", (LoginRequest request, AccountStore store) =>
{
    var success = store.TryAuthenticate(request.Username, request.Password, out var user);
    return Results.Ok(new AuthenticationResponse
    {
        Success = success,
        User = user is null ? null : ToAccountDto(user)
    });
})
    .WithTags("Accounts")
    .WithName("AuthenticateAccount")
    .WithSummary("Authenticate an account")
    .WithDescription("Checks the supplied username and password. A failed login is represented by Success=false in the response; it is not returned as an HTTP 401.")
    .Produces<AuthenticationResponse>(StatusCodes.Status200OK);

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
        User = user is null ? null : ToAccountDto(user),
        Error = error
    });
})
    .WithTags("Accounts")
    .WithName("CreateAccount")
    .WithSummary("Create an account")
    .WithDescription("Attempts to create an account using the supplied display name, username, and password. Check Success and Error in the response.")
    .Produces<CreateAccountResponse>(StatusCodes.Status200OK);

app.MapGet("/api/accounts", (AccountStore store) =>
    Results.Ok(store.GetAllAccounts().Select(ToAccountDto).ToList()))
    .WithTags("Accounts")
    .WithName("ListAccounts")
    .WithSummary("List accounts")
    .WithDescription("Returns account records as AccountDto objects. Password hashes are not included.")
    .Produces<List<AccountDto>>(StatusCodes.Status200OK);

app.MapGet("/api/accounts/{id:int}/active", (int id, AccountStore store) =>
    Results.Ok(store.IsActiveAccount(id)))
    .WithTags("Accounts")
    .WithName("GetAccountActiveStatus")
    .WithSummary("Check whether an account is active")
    .WithDescription("Returns whether the account with the supplied numeric ID is active.")
    .Produces<bool>(StatusCodes.Status200OK);

app.MapPost("/api/accounts/{id:int}/delete", (int id, DeleteAccountRequest request, AccountStore store) =>
{
    var success = store.TrySoftDeleteAccount(id, request.Password, out var error);
    return Results.Ok(new OperationResponse { Success = success, Error = error });
})
    .WithTags("Accounts")
    .WithName("SoftDeleteAccount")
    .WithSummary("Soft-delete an account")
    .WithDescription("Verifies the account password and marks the account as deleted without physically removing its database record.")
    .Produces<OperationResponse>(StatusCodes.Status200OK);

app.MapGet("/api/incidents", (MariaDbDataStore store) =>
    Results.Ok(store.LoadIncidents().Select(ToIncidentDto).ToList()))
    .WithTags("Incidents")
    .WithName("ListIncidents")
    .WithSummary("List incidents")
    .WithDescription("Returns the incidents currently stored by the backend.")
    .Produces<List<IncidentDto>>(StatusCodes.Status200OK);

app.MapGet("/api/history", (MariaDbDataStore store) =>
    Results.Ok(store.LoadCheckHistory().Select(ToHistoryDto).ToList()))
    .WithTags("History")
    .WithName("GetCheckHistory")
    .WithSummary("Get endpoint check history")
    .WithDescription("Returns the latest 200 endpoint check history entries for the desktop application.")
    .Produces<List<CheckHistoryEntryDto>>(StatusCodes.Status200OK);

app.MapPut("/api/incidents", (List<IncidentDto> items, MariaDbDataStore store) =>
{
    store.SaveIncidents(items.Select(FromIncidentDto));
    return Results.Ok(new OperationResponse { Success = true });
})
    .WithTags("Incidents")
    .WithName("SaveIncidents")
    .WithSummary("Save a collection of incidents")
    .WithDescription("Saves the supplied incident collection through the data store and returns an operation result.")
    .Produces<OperationResponse>(StatusCodes.Status200OK);

app.MapPut("/api/incidents/item", (IncidentSaveRequest request, MariaDbDataStore store) =>
{
    var success = store.SaveIncident(FromIncidentDto(request.Incident), request.AllowInsert);
    return Results.Ok(new OperationResponse
    {
        Success = success,
        Error = success ? string.Empty : "The incident was not found and insertion was not allowed."
    });
})
    .WithTags("Incidents")
    .WithName("SaveIncident")
    .WithSummary("Save one incident")
    .WithDescription("Saves the supplied incident. A new incident is inserted only when AllowInsert is true.")
    .Produces<OperationResponse>(StatusCodes.Status200OK);

app.MapPut("/api/history/item", (CheckHistoryEntryDto item, MariaDbDataStore store) =>
{
    var entry = FromHistoryDto(item);
    var success = store.SaveCheckHistoryEntry(entry);
    return Results.Ok(new OperationResponse
    {
        Success = success,
        Id = success ? entry.Id : null,
        Error = success ? string.Empty : "The history entry no longer exists."
    });
})
    .WithTags("History")
    .WithName("SaveCheckHistoryEntry")
    .WithSummary("Save one check history entry")
    .WithDescription("Saves a history entry and returns its database ID when the operation succeeds.")
    .Produces<OperationResponse>(StatusCodes.Status200OK);

app.MapPost("/api/security/events", (
    SecurityEventRequest request,
    SecurityAuditLogger logger,
    AccountStore accounts) =>
{
    if (request.EventType == "flush")
    {
        logger.FlushPendingEvents();
        return Results.Ok(new OperationResponse { Success = true });
    }

    if (request.EventType == "login-failed")
    {
        logger.RecordFailedLogin(
            request.AttemptedUsername,
            request.Category ?? "Sign-in failed",
            request.Reason ?? "No additional information was supplied.",
            Enum.IsDefined(typeof(IncidentSeverity), request.Severity)
                ? (IncidentSeverity)request.Severity
                : IncidentSeverity.Medium);

        return Results.Ok(new OperationResponse { Success = true });
    }

    var user = request.UserId is > 0
        ? accounts.GetForAudit(request.UserId.Value)
        : null;

    if (user is null)
    {
        return Results.BadRequest(new OperationResponse
        {
            Success = false,
            Error = "A valid account ID is required for this security event."
        });
    }

    switch (request.EventType)
    {
        case "login-success":
            if (user.IsDeleted)
                return Results.Conflict(new OperationResponse
                {
                    Success = false,
                    Error = "A deleted account cannot record a successful login."
                });
            logger.RecordSuccessfulLogin(user, request.Context);
            break;

        case "account-created":
            if (user.IsDeleted)
                return Results.Conflict(new OperationResponse
                {
                    Success = false,
                    Error = "A deleted account cannot record an account-created event."
                });
            logger.RecordAccountCreated(user);
            break;

        case "logout":
            logger.RecordSuccessfulLogout(user, request.Context);
            break;

        case "account-deleted":
            logger.RecordAccountDeleted(user);
            break;

        case "session-revoked":
            logger.RecordSessionRevoked(user, request.Context ?? string.Empty);
            break;

        default:
            return Results.BadRequest(new OperationResponse
            {
                Success = false,
                Error = "Unknown security event type."
            });
    }

    return Results.Ok(new OperationResponse { Success = true });
})
    .WithTags("Security")
    .WithName("RecordSecurityEvent")
    .WithSummary("Record a security audit event")
    .WithDescription("Supported EventType values: login-failed, login-success, account-created, logout, account-deleted, session-revoked, and flush. Events other than login-failed and flush require a valid UserId. Deleted accounts cannot record login-success or account-created events.")
    .Produces<OperationResponse>(StatusCodes.Status200OK)
    .Produces<OperationResponse>(StatusCodes.Status400BadRequest)
    .Produces<OperationResponse>(StatusCodes.Status409Conflict);

app.MapPost("/api/monitoring/check", async (
    MonitoringCheckRequest request,
    EndpointMonitoringService monitoring,
    CancellationToken cancellationToken) =>
{
    var result = await monitoring.CheckAsync(request.Address, cancellationToken);
    return Results.Ok(result);
})
    .WithTags("Monitoring")
    .WithName("CheckEndpoint")
    .WithSummary("Check an endpoint")
    .WithDescription("Sends an HTTP GET request to the supplied HTTP/HTTPS address, measures response time, and records the result in endpoint history. Failures may create or update an incident. The monitoring outcome is returned in MonitoringResult.")
    .Produces<MonitoringResult>(StatusCodes.Status200OK);

app.Run();

static AccountDto ToAccountDto(AccountUser user) => new()
{
    Id = user.Id,
    DisplayName = user.DisplayName,
    Username = user.Username,
    CreatedAt = user.CreatedAt,
    IsDeleted = user.IsDeleted,
    DeletedAt = user.DeletedAt
};

static IncidentDto ToIncidentDto(Incident item) => new()
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

static Incident FromIncidentDto(IncidentDto item) => new()
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

static CheckHistoryEntryDto ToHistoryDto(CheckHistoryEntry item) => new()
{
    Id = item.Id,
    Timestamp = item.Timestamp,
    Endpoint = item.Endpoint,
    Result = item.Result,
    Details = item.Details,
    ResponseTimeMs = item.ResponseTimeMs
};

static CheckHistoryEntry FromHistoryDto(CheckHistoryEntryDto item) => new()
{
    Id = item.Id,
    Timestamp = item.Timestamp,
    Endpoint = item.Endpoint,
    Result = item.Result,
    Details = item.Details,
    ResponseTimeMs = item.ResponseTimeMs
};
