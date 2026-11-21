namespace AlertingSystem.Models;

public sealed class AccountUser
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    public string StatusDisplay => IsDeleted ? "DELETED" : "ACTIVE";

    public string LifecycleDisplay =>
        IsDeleted && DeletedAt is DateTime deletedAt
            ? $"Deleted {deletedAt:yyyy-MM-dd HH:mm:ss} UTC"
            : $"Created {CreatedAt:yyyy-MM-dd HH:mm:ss}";
}
