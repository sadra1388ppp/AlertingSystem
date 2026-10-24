namespace AlertingSystem.Models;

/// <summary>
/// User-owned schedule configuration kept locally outside the monitoring database.
/// </summary>
public sealed class ScheduleConfiguration
{
    public List<string> Endpoints { get; set; } = [];
    public int IntervalMinutes { get; set; } = 5;
    public bool IsEnabled { get; set; }
    public DateTime? NextRunAt { get; set; }
}
