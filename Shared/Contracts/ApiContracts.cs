namespace AlertingSystem.Contracts;

public sealed class AccountDto
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
}

public sealed class IncidentDto
{
    public string IncidentNumber { get; set; } = string.Empty;
    public string EndpointUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string ResolutionNote { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public int Severity { get; set; }
    public int Status { get; set; }
    public DateTime FirstOccurredAt { get; set; }
    public DateTime LastOccurredAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public int OccurrenceCount { get; set; }
}

public sealed class CheckHistoryEntryDto
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public long? ResponseTimeMs { get; set; }
}

public sealed class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class AuthenticationResponse
{
    public bool Success { get; set; }
    public AccountDto? User { get; set; }
}

public sealed class CreateAccountRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class CreateAccountResponse
{
    public bool Success { get; set; }
    public AccountDto? User { get; set; }
    public string Error { get; set; } = string.Empty;
}

public sealed class DeleteAccountRequest
{
    public string Password { get; set; } = string.Empty;
}

public sealed class OperationResponse
{
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public long? Id { get; set; }
}

public sealed class IncidentSaveRequest
{
    public IncidentDto Incident { get; set; } = new();
    public bool AllowInsert { get; set; }
}

public sealed class SecurityEventRequest
{
    public string EventType { get; set; } = string.Empty;
    public AccountDto? User { get; set; }
    public string? Context { get; set; }
    public string? AttemptedUsername { get; set; }
    public string? Category { get; set; }
    public string? Reason { get; set; }
    public int Severity { get; set; }
}

public sealed class MonitoringCheckRequest
{
    public string Address { get; set; } = string.Empty;
}

public sealed class MonitoringResult
{
    public string Status { get; set; } = string.Empty;
    public string BrushKey { get; set; } = "NeutralBrush";
    public string Message { get; set; } = string.Empty;
    public string ResponseTime { get; set; } = "—";
    public string ResponseSummary { get; set; } = "—";
    public long? ResponseTimeMs { get; set; }
}
