using System.IO;
using System.Text.Json;
using AlertingSystem.Models;

namespace AlertingSystem.Services;

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
            var json = File.ReadAllText(_path);
            var configuration = JsonSerializer.Deserialize<ScheduleConfiguration>(json, JsonOptions);

            if (configuration is null)
            {
                return new ScheduleConfiguration();
            }

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(nameof(ScheduleConfiguration.IntervalSeconds), out _) &&
                document.RootElement.TryGetProperty("IntervalMinutes", out var legacyMinutes) &&
                legacyMinutes.TryGetInt32(out var minutes))
            {
                configuration.IntervalSeconds = Math.Clamp(minutes, 1, 1440) * 60;
            }

            configuration.Endpoints ??= [];
            configuration.Endpoints = configuration.Endpoints
                .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
                .Select(endpoint => endpoint.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            configuration.IntervalSeconds = Math.Clamp(configuration.IntervalSeconds, 1, 86400);
            return configuration;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
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
