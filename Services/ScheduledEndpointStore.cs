using System.IO;
using System.Text.Json;
using AlertingSystem.Models;

namespace AlertingSystem.Services;

/// <summary>
/// Persists endpoint schedules in the user's local application data folder.
/// Scheduler configuration does not change or migrate the MariaDB schema.
/// </summary>
public sealed class ScheduledEndpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AlertingSystem",
        "scheduled-endpoints.json");

    public ScheduleConfiguration Load()
    {
        if (!File.Exists(_path))
        {
            return new ScheduleConfiguration();
        }

        try
        {
            var configuration = JsonSerializer.Deserialize<ScheduleConfiguration>(
                File.ReadAllText(_path), JsonOptions);

            if (configuration is null)
            {
                return new ScheduleConfiguration();
            }

            configuration.Endpoints ??= [];
            configuration.Endpoints = configuration.Endpoints
                .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
                .Select(endpoint => endpoint.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            configuration.IntervalMinutes = Math.Clamp(configuration.IntervalMinutes, 1, 1440);
            return configuration;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged local schedule must not prevent the application from opening.
            return new ScheduleConfiguration();
        }
    }

    public void Save(ScheduleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);

        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configuration, JsonOptions));
        File.Move(temporaryPath, _path, overwrite: true);
    }
}
