namespace AlertingSystem.Models;

public sealed class ScheduleConfiguration
{
    public List<string> Endpoints { get; set; } = [];
    public int IntervalSeconds { get; set; } = 300;
    public bool IsEnabled { get; set; }
    public DateTime? NextRunAt { get; set; }
}
