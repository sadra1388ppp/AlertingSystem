using System.Windows;
using System.Windows.Controls;

namespace AlertingSystem;

public partial class MainWindow
{
    private void OverviewNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(OverviewPage, OverviewNavButton);
    }

    private void IncidentsNavButton_Click(object sender, RoutedEventArgs e)
    {
        _showAllIncidents = false;
        RefreshIncidentUI();
        ShowPage(IncidentsPage, IncidentsNavButton);
    }

    private void AlertHistoryNavButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshCheckHistoryUI();
        ShowPage(AlertHistoryPage, AlertHistoryNavButton);
    }

    private void AccountNavButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshAccountDirectory();
        ShowPage(AccountPage, AccountNavButton);
    }

    private void AccountDirectoryTextBlock_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock textBlock)
        {
            // Bound values in the account directory can be Persian or English.
            TextDirectionHelper.Apply(textBlock, textBlock.Text);
        }
    }

    private void RefreshAccountDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshAccountDirectory();
    }

    private void RefreshAccountDirectory()
    {
        try
        {
            var accounts = _accountStore.GetAllAccounts();
            AccountDirectoryList.ItemsSource = accounts;
            var deletedCount = accounts.Count(account => account.IsDeleted);
            AccountDirectoryCountText.Text =
                $"{accounts.Count} accounts  •  {deletedCount} deleted";
        }
        catch (Exception exception)
        {
            AccountDirectoryList.ItemsSource = null;
            AccountDirectoryCountText.Text = "Account list unavailable";
            MessageBox.Show(
                this,
                $"The account list could not be loaded from MariaDB. No account data was deleted.\n\n{exception.Message}",
                "Account directory warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void CreateAnotherAccountButton_Click(object sender, RoutedEventArgs e)
    {
        var registrationWindow = new LoginWindow(openRegistration: true, registrationOnly: true)
        {
            Owner = this
        };
        registrationWindow.ShowDialog();
    }

    private void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionEnding)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "Sign out of AlertingSystem? This action will be recorded in Alert History.",
            "Sign out",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _sessionEnding = true;
        _securityAuditLogger.RecordSuccessfulLogout(_currentUser);
        EndSessionAndRequireLogin();
    }

    private void DeleteAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionEnding)
        {
            return;
        }

        var confirmation = new DeleteAccountWindow(_currentUser.Username)
        {
            Owner = this
        };

        if (confirmation.ShowDialog() != true)
        {
            return;
        }

        try
        {
            if (!_accountStore.TrySoftDeleteAccount(
                    _currentUser.Id,
                    confirmation.ConfirmedPassword,
                    out var error))
            {
                MessageBox.Show(
                    this,
                    error,
                    "Account not deleted",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _sessionEnding = true;
            _securityAuditLogger.RecordAccountDeleted(_currentUser);
            _securityAuditLogger.RecordSuccessfulLogout(
                _currentUser,
                "Session ended because the account was soft-deleted.");

            MessageBox.Show(
                this,
                "Your account has been marked as deleted. Its Accounts row remains in MariaDB with IsDeleted = 1 and DeletedAt set. Your incidents and alert history have not been removed.",
                "Account deleted",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            EndSessionAndRequireLogin();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"The account could not be deleted. MariaDB did not confirm the change.\n\n{exception.Message}",
                "Account deletion failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void EndSessionAndRequireLogin()
    {
        _databaseSyncTimer.Stop();
        _securityAlertToastTimer.Stop();

        var loginWindow = new LoginWindow();
        Hide();
        var result = loginWindow.ShowDialog();

        if (result == true && loginWindow.AuthenticatedUser is not null)
        {
            var nextWindow = new MainWindow(loginWindow.AuthenticatedUser);
            Application.Current.MainWindow = nextWindow;
            nextWindow.Show();
        }

        Close();
    }

    private sealed class UiPreferences
    {
        public string Theme { get; set; } = "Dark";
    }

    private string UiPreferencesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AlertingSystem",
        "ui-settings.json");
}
