using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AlertingSystem;

public partial class MainWindow
{
    private void LoadThemePreference()
    {
        var theme = "Dark";
        try
        {
            if (File.Exists(UiPreferencesPath))
            {
                var preferences = System.Text.Json.JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(UiPreferencesPath));
                if (string.Equals(preferences?.Theme, "Light", StringComparison.OrdinalIgnoreCase))
                {
                    theme = "Light";
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            theme = "Dark";
        }

        ApplyTheme(theme);
    }

    private void SaveThemePreference()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UiPreferencesPath)!);
            var preferences = new UiPreferences { Theme = _selectedTheme };
            var json = System.Text.Json.JsonSerializer.Serialize(preferences, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(UiPreferencesPath, json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, $"The theme changed for this session, but the preference could not be saved.\n\n{exception.Message}",
                "Theme preference warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyTheme(string theme)
    {
        _isLightTheme = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
        _selectedTheme = _isLightTheme ? "Light" : "Dark";

        var applicationResources = Application.Current.Resources;

        foreach (var entry in ThemeRoleColors)
        {
            var color = _isLightTheme ? entry.Value.Light : entry.Value.Dark;
            var brush = new SolidColorBrush(ParseThemeColor(color));
            Resources[entry.Key] = brush;
            applicationResources[entry.Key] = brush;
        }

        foreach (var darkColor in ThemeLiteralColors)
        {
            var color = darkColor;
            if (_isLightTheme && LightLiteralColorOverrides.TryGetValue(darkColor, out var lightColor))
                color = lightColor;
            else if (!_isLightTheme && DarkLiteralColorOverrides.TryGetValue(darkColor, out var darkOverride))
                color = darkOverride;

            var key = "Hex" + darkColor.TrimStart('#') + "Brush";
            var brush = new SolidColorBrush(ParseThemeColor(color));
            Resources[key] = brush;
            applicationResources[key] = brush;
        }

        applicationResources[SystemColors.WindowBrushKey] = new SolidColorBrush(
            ParseThemeColor(_isLightTheme ? "#FFFFFF" : "#07151D"));
        applicationResources[SystemColors.WindowTextBrushKey] = new SolidColorBrush(
            ParseThemeColor(_isLightTheme ? "#172D37" : "#E8F6F8"));
        applicationResources[SystemColors.ControlBrushKey] = new SolidColorBrush(
            ParseThemeColor(_isLightTheme ? "#E4F1F4" : "#16333F"));
        applicationResources[SystemColors.ControlTextBrushKey] = new SolidColorBrush(
            ParseThemeColor(_isLightTheme ? "#172D37" : "#E8F6F8"));
        applicationResources[SystemColors.HighlightBrushKey] = new SolidColorBrush(
            ParseThemeColor(_isLightTheme ? "#C5F0F2" : "#104451"));
        applicationResources[SystemColors.HighlightTextBrushKey] = new SolidColorBrush(
            ParseThemeColor(_isLightTheme ? "#172D37" : "#E8F6F8"));


        StatusIndicator.Fill = (Brush)FindResource(_currentStatusBrushKey);
        OpenIncidentsFilterButton.Background = _showAllIncidents
            ? (Brush)FindResource("SecondaryButtonBrush")
            : (Brush)FindResource("SelectedFilterBrush");
        AllIncidentsFilterButton.Background = _showAllIncidents
            ? (Brush)FindResource("SelectedFilterBrush")
            : (Brush)FindResource("SecondaryButtonBrush");

        ThemeDescriptionText.Text = _isLightTheme
            ? "Light theme is active. Your preference is saved locally on this computer."
            : "Dark theme is active. Your preference is saved locally on this computer.";

        SyncThemeOptionSelection();
        if (_activePage is not null && _activeNavButton is not null)
        {
            UpdateNavigationSelection();
        }
    }

    private static Color ParseThemeColor(string value) =>
        (Color)System.Windows.Media.ColorConverter.ConvertFromString(value)!;

    private void SyncThemeOptionSelection()
    {
        _isUpdatingThemeOptions = true;
        DarkThemeOption.IsChecked = !_isLightTheme;
        LightThemeOption.IsChecked = _isLightTheme;
        _isUpdatingThemeOptions = false;
    }

    private void DarkThemeOption_Checked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingThemeOptions || _selectedTheme == "Dark") return;
        ApplyTheme("Dark");
        SaveThemePreference();
    }

    private void LightThemeOption_Checked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingThemeOptions || _selectedTheme == "Light") return;
        ApplyTheme("Light");
        SaveThemePreference();
    }
    private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(SettingsPage, SettingsNavButton);
    }

    private void ShowPage(Grid selectedPage, Button selectedButton)
    {
        OverviewPage.Visibility = Visibility.Collapsed;
        IncidentsPage.Visibility = Visibility.Collapsed;
        AlertHistoryPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        AccountPage.Visibility = Visibility.Collapsed;
        selectedPage.Visibility = Visibility.Visible;

        _activePage = selectedPage;
        _activeNavButton = selectedButton;
        UpdateNavigationSelection();
    }

    private void UpdateNavigationSelection()
    {
        if (_activeNavButton is null)
        {
            return;
        }

        foreach (var button in new[] { OverviewNavButton, IncidentsNavButton, AlertHistoryNavButton, AccountNavButton, SettingsNavButton })
        {
            var isSelected = button == _activeNavButton;
            button.Tag = isSelected;
            button.Background = isSelected
                ? (Brush)FindResource("Hex20385FBrush")
                : Brushes.Transparent;
            button.BorderBrush = isSelected
                ? (Brush)FindResource("Hex3B82F6Brush")
                : Brushes.Transparent;
            button.BorderThickness = new Thickness(isSelected ? 1 : 0);
            button.Foreground = isSelected
                ? (Brush)FindResource("HexEAF2FFBrush")
                : (Brush)FindResource("MutedBrush");
            button.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
}
