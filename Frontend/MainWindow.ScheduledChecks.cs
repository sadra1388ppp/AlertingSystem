using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AlertingSystem.Models;
using AlertingSystem.Services;
using Microsoft.EntityFrameworkCore;
namespace AlertingSystem;

public partial class MainWindow
{
    private void InitializeScheduledChecks()
    {
        _scheduleConfiguration = _scheduledEndpointStore.Load();
        ScheduleEndpointsTextBox.Text = string.Join(Environment.NewLine, _scheduleConfiguration.Endpoints);
        SetScheduleIntervalControls(_scheduleConfiguration.IntervalSeconds);
        ScheduledEndpointsList.ItemsSource = _scheduledEndpointStatuses;
        RebuildScheduledEndpointStatuses(_scheduleConfiguration.Endpoints);

        if (_scheduleConfiguration.IsEnabled && _scheduleConfiguration.Endpoints.Count > 0)
        {
            _scheduleConfiguration.NextRunAt ??= DateTime.Now.AddSeconds(_scheduleConfiguration.IntervalSeconds);
            _scheduledCheckTimer.Start();
        }
        else if (_scheduleConfiguration.IsEnabled)
        {
            _scheduleConfiguration.IsEnabled = false;
            _scheduleConfiguration.NextRunAt = null;
            SaveScheduleConfigurationSafely();
        }

        UpdateScheduledChecksUI();
    }

    private void SetScheduleIntervalControls(int intervalSeconds)
    {
        _scheduleIntervalIsMinutes = intervalSeconds >= 60 && intervalSeconds % 60 == 0;
        ScheduleIntervalTextBox.Text = (_scheduleIntervalIsMinutes
            ? intervalSeconds / 60
            : intervalSeconds).ToString();
        UpdateScheduleUnitSelector();
    }

    private void ScheduleSecondsUnitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scheduledBatchRunning)
        {
            return;
        }

        _scheduleIntervalIsMinutes = false;
        UpdateScheduleUnitSelector();
    }

    private void ScheduleMinutesUnitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scheduledBatchRunning)
        {
            return;
        }

        _scheduleIntervalIsMinutes = true;
        UpdateScheduleUnitSelector();
    }

    private void UpdateScheduleUnitSelector()
    {
        var selectedBackground = (Brush)FindResource("Hex20385FBrush");
        var selectedBorder = (Brush)FindResource("Hex477DFABrush");
        var selectedForeground = (Brush)FindResource("HexEAF2FFBrush");
        var idleForeground = (Brush)FindResource("MutedBrush");

        ScheduleSecondsUnitButton.Background = _scheduleIntervalIsMinutes
            ? Brushes.Transparent
            : selectedBackground;
        ScheduleSecondsUnitButton.BorderBrush = _scheduleIntervalIsMinutes
            ? Brushes.Transparent
            : selectedBorder;
        ScheduleSecondsUnitButton.Foreground = _scheduleIntervalIsMinutes
            ? idleForeground
            : selectedForeground;
        ScheduleSecondsUnitButton.FontWeight = _scheduleIntervalIsMinutes
            ? FontWeights.Medium
            : FontWeights.SemiBold;

        ScheduleMinutesUnitButton.Background = _scheduleIntervalIsMinutes
            ? selectedBackground
            : Brushes.Transparent;
        ScheduleMinutesUnitButton.BorderBrush = _scheduleIntervalIsMinutes
            ? selectedBorder
            : Brushes.Transparent;
        ScheduleMinutesUnitButton.Foreground = _scheduleIntervalIsMinutes
            ? selectedForeground
            : idleForeground;
        ScheduleMinutesUnitButton.FontWeight = _scheduleIntervalIsMinutes
            ? FontWeights.SemiBold
            : FontWeights.Medium;
    }

    private static string FormatInterval(int intervalSeconds)
    {
        if (intervalSeconds >= 60 && intervalSeconds % 60 == 0)
        {
            var minutes = intervalSeconds / 60;
            return $"{minutes} minute{(minutes == 1 ? string.Empty : "s")}";
        }

        return $"{intervalSeconds} second{(intervalSeconds == 1 ? string.Empty : "s")}";
    }

    private static string FormatNextRun(DateTime nextRun)
    {
        var remaining = nextRun - DateTime.Now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var countdown = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";

        return $"In {countdown}  •  {nextRun:HH:mm:ss}";
    }

    private void SaveScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (SaveScheduleInputs())
        {
            MessageBox.Show(this,
                "Your endpoint list and interval have been saved on this computer.",
                "Schedule saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private bool SaveScheduleInputs()
    {
        var rawEndpoints = ScheduleEndpointsTextBox.Text
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (rawEndpoints.Count == 0)
        {
            MessageBox.Show(this, "Add at least one URL or domain, with one endpoint per line.",
                "No endpoints", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var endpoints = new List<string>();
        var invalidEndpoints = new List<string>();
        foreach (var raw in rawEndpoints)
        {
            if (!TryNormalizeScheduledEndpoint(raw, out var normalized))
            {
                invalidEndpoints.Add(raw);
                continue;
            }

            if (!endpoints.Contains(normalized, StringComparer.Ordinal))
            {
                endpoints.Add(normalized);
            }
        }

        if (invalidEndpoints.Count > 0)
        {
            MessageBox.Show(this,
                "These entries are not valid HTTP/HTTPS URLs or domains:\n\n" +
                string.Join(Environment.NewLine, invalidEndpoints.Take(10)) +
                (invalidEndpoints.Count > 10 ? "\n…" : string.Empty),
                "Check endpoint list", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!int.TryParse(ScheduleIntervalTextBox.Text.Trim(), out var intervalValue) ||
            intervalValue < 1)
        {
            MessageBox.Show(this, "Enter a positive whole number for the check interval.",
                "Invalid interval", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var intervalIsMinutes = _scheduleIntervalIsMinutes;
        var maximumValue = intervalIsMinutes ? 1440 : 86400;
        if (intervalValue > maximumValue)
        {
            MessageBox.Show(this,
                intervalIsMinutes
                    ? "The maximum interval is 1440 minutes (24 hours)."
                    : "The maximum interval is 86400 seconds (24 hours).",
                "Invalid interval", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var intervalSeconds = intervalIsMinutes ? intervalValue * 60 : intervalValue;
        _scheduleConfiguration.Endpoints = endpoints;
        _scheduleConfiguration.IntervalSeconds = intervalSeconds;
        if (_scheduleConfiguration.IsEnabled)
        {
            _scheduleConfiguration.NextRunAt = DateTime.Now.AddSeconds(intervalSeconds);
        }

        ScheduleEndpointsTextBox.Text = string.Join(Environment.NewLine, endpoints);
        RebuildScheduledEndpointStatuses(endpoints);

        if (!SaveScheduleConfigurationSafely())
        {
            return false;
        }

        UpdateScheduledChecksUI();
        return true;
    }

    private static bool TryNormalizeScheduledEndpoint(string endpoint, out string normalized)
    {
        normalized = string.Empty;
        var candidate = endpoint.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"https://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
    }

    private void RebuildScheduledEndpointStatuses(IEnumerable<string> endpoints)
    {
        var previousStatuses = _scheduledEndpointStatuses.ToDictionary(
            item => item.Endpoint, StringComparer.Ordinal);
        var recentHistory = _checkHistory
            .OrderByDescending(entry => entry.Timestamp)
            .ToList();

        _scheduledEndpointStatuses.Clear();
        foreach (var endpoint in endpoints.Distinct(StringComparer.Ordinal))
        {
            if (previousStatuses.TryGetValue(endpoint, out var previous))
            {
                _scheduledEndpointStatuses.Add(previous);
                continue;
            }

            var status = new ScheduledEndpointStatus(endpoint);
            var latestCheck = recentHistory.FirstOrDefault(entry =>
                string.Equals(
                    NormalizeEndpoint(entry.Endpoint),
                    NormalizeEndpoint(endpoint),
                    StringComparison.OrdinalIgnoreCase));

            if (latestCheck is not null)
            {
                status.LastResult = latestCheck.Result.StartsWith("HTTP 2", StringComparison.OrdinalIgnoreCase)
                    ? "Healthy"
                    : latestCheck.Result;
                status.LastCheckedAt = latestCheck.Timestamp;
                status.ResponseTimeMs = latestCheck.ResponseTimeMs;
            }

            _scheduledEndpointStatuses.Add(status);
        }

        ScheduledEndpointCountText.Text = $"{_scheduledEndpointStatuses.Count} endpoint(s)";
        ScheduledEndpointsEmptyText.Visibility = _scheduledEndpointStatuses.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void StartScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scheduledBatchRunning || !SaveScheduleInputs())
        {
            return;
        }

        _scheduleConfiguration.IsEnabled = true;
        _scheduleConfiguration.NextRunAt = DateTime.Now.AddSeconds(_scheduleConfiguration.IntervalSeconds);
        if (!SaveScheduleConfigurationSafely())
        {
            _scheduleConfiguration.IsEnabled = false;
            _scheduleConfiguration.NextRunAt = null;
            UpdateScheduledChecksUI();
            return;
        }

        _lastScheduledScanSummary = string.Empty;
        _scheduledCheckTimer.Start();
        UpdateScheduledChecksUI();
    }

    private void PauseScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scheduledBatchRunning)
        {
            return;
        }

        _scheduleConfiguration.IsEnabled = false;
        _scheduleConfiguration.NextRunAt = null;
        _scheduledCheckTimer.Stop();
        SaveScheduleConfigurationSafely();
        UpdateScheduledChecksUI();
    }

    private async void RunScheduledNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scheduledBatchRunning || !SaveScheduleInputs())
        {
            return;
        }

        await RunScheduledBatchAsync();
    }

    private async void ScheduledCheckTimer_Tick(object? sender, EventArgs e)
    {
        if (!_scheduleConfiguration.IsEnabled || _scheduledBatchRunning)
        {
            return;
        }

        if (_scheduledEndpointStatuses.Count == 0)
        {
            _scheduleConfiguration.IsEnabled = false;
            _scheduleConfiguration.NextRunAt = null;
            _scheduledCheckTimer.Stop();
            SaveScheduleConfigurationSafely();
            UpdateScheduledChecksUI();
            return;
        }

        if (_scheduleConfiguration.NextRunAt is null)
        {
            _scheduleConfiguration.NextRunAt = DateTime.Now.AddSeconds(_scheduleConfiguration.IntervalSeconds);
            SaveScheduleConfigurationSafely();
            UpdateScheduledChecksUI();
            return;
        }

        if (DateTime.Now >= _scheduleConfiguration.NextRunAt.Value)
        {
            await RunScheduledBatchAsync();
        }
        else
        {
            ScheduleNextRunText.Text = FormatNextRun(_scheduleConfiguration.NextRunAt.Value);
        }
    }

    private async Task RunScheduledBatchAsync()
    {
        if (_scheduledBatchRunning)
        {
            return;
        }

        var endpoints = _scheduledEndpointStatuses.ToList();
        if (endpoints.Count == 0)
        {
            MessageBox.Show(this, "Save at least one endpoint before running a scan.",
                "No endpoints", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _scheduledBatchRunning = true;
        UpdateScheduledChecksUI();

        try
        {
            for (var index = 0; index < endpoints.Count; index++)
            {
                var endpoint = endpoints[index];
                ScheduleStatusText.Text = $"Checking {index + 1} of {endpoints.Count}: {endpoint.Endpoint}";
                endpoint.LastResult = "Checking…";

                var outcome = await PerformEndpointCheckAsync(endpoint.Endpoint, updateManualUi: false);
                endpoint.LastResult = outcome.Status;
                endpoint.LastCheckedAt = DateTime.Now;
                endpoint.ResponseTimeMs = outcome.ResponseTimeMs;
            }

            _lastScheduledScanSummary = $"Last scan completed at {DateTime.Now:HH:mm:ss} ({endpoints.Count} endpoints checked).";
        }
        catch (Exception exception)
        {
            _lastScheduledScanSummary = $"Scan stopped after an unexpected error: {exception.Message}";
        }
        finally
        {
            _scheduledBatchRunning = false;

            if (_scheduleConfiguration.IsEnabled)
            {
                _scheduleConfiguration.NextRunAt = DateTime.Now.AddSeconds(_scheduleConfiguration.IntervalSeconds);
            }

            SaveScheduleConfigurationSafely();
            UpdateScheduledChecksUI();
        }
    }

    private bool SaveScheduleConfigurationSafely()
    {
        try
        {
            _scheduledEndpointStore.Save(_scheduleConfiguration);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this,
                $"The schedule could not be saved locally. Your current session can continue, but this change may not survive an app restart.\n\n{exception.Message}",
                "Schedule storage warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void UpdateScheduledChecksUI()
    {
        ScheduledEndpointCountText.Text = $"{_scheduledEndpointStatuses.Count} endpoint(s)";
        ScheduledEndpointsEmptyText.Visibility = _scheduledEndpointStatuses.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        SaveScheduleButton.IsEnabled = !_scheduledBatchRunning;
        ScheduleEndpointsTextBox.IsEnabled = !_scheduledBatchRunning;
        ScheduleIntervalTextBox.IsEnabled = !_scheduledBatchRunning;
        ScheduleSecondsUnitButton.IsEnabled = !_scheduledBatchRunning;
        ScheduleMinutesUnitButton.IsEnabled = !_scheduledBatchRunning;
        RunScheduledNowButton.IsEnabled = !_scheduledBatchRunning && _scheduledEndpointStatuses.Count > 0;
        StartScheduleButton.IsEnabled = !_scheduledBatchRunning && !_scheduleConfiguration.IsEnabled;
        PauseScheduleButton.IsEnabled = !_scheduledBatchRunning && _scheduleConfiguration.IsEnabled;

        if (_scheduledBatchRunning)
        {
            ScheduleStatusBadgeText.Text = "SCANNING";
            ScheduleStatusBadgeText.Foreground = (Brush)FindResource("HexEAF2FFBrush");
            ScheduleStatusIndicator.Fill = (Brush)FindResource("NeutralBrush");
            ScheduleNextRunText.Text = "Scan in progress";
            return;
        }

        if (_scheduleConfiguration.IsEnabled)
        {
            ScheduleStatusBadgeText.Text = "ACTIVE";
            ScheduleStatusBadgeText.Foreground = (Brush)FindResource("SuccessBrush");
            ScheduleStatusIndicator.Fill = (Brush)FindResource("SuccessBrush");
            ScheduleNextRunText.Text = _scheduleConfiguration.NextRunAt is DateTime nextRun
                ? FormatNextRun(nextRun)
                : "Calculating…";
            ScheduleStatusText.Text = string.IsNullOrWhiteSpace(_lastScheduledScanSummary)
                ? $"Automatic checks are active every {FormatInterval(_scheduleConfiguration.IntervalSeconds)} after the previous scan finishes."
                : _lastScheduledScanSummary;
        }
        else
        {
            ScheduleStatusBadgeText.Text = "PAUSED";
            ScheduleStatusBadgeText.Foreground = (Brush)FindResource("MutedBrush");
            ScheduleStatusIndicator.Fill = (Brush)FindResource("WarningBrush");
            ScheduleNextRunText.Text = "Not scheduled";
            ScheduleStatusText.Text = _scheduledEndpointStatuses.Count == 0
                ? "Add endpoints and save the list to configure monitoring."
                : "Schedule paused. Use Run all now to run a one-time scan.";
        }
    }
}
