namespace AlertingSystem.Models;

public sealed class AccountUser
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
