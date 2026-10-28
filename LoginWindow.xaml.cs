using System.Windows;
using System.Windows.Input;
using AlertingSystem.Models;
using AlertingSystem.Services;

namespace AlertingSystem;

public partial class LoginWindow : Window
{
    private readonly AccountStore _accountStore;
    private readonly bool _registrationOnly;
    private bool _isRegisterMode;

    public AccountUser? AuthenticatedUser { get; private set; }

    public LoginWindow(bool openRegistration = false, bool registrationOnly = false)
    {
        InitializeComponent();
        _registrationOnly = registrationOnly;
        _accountStore = new AccountStore();

        var hasAccounts = _accountStore.HasAccounts();
        SetMode(openRegistration || !hasAccounts);

        if (!hasAccounts && !registrationOnly)
        {
            FeedbackText.Text = "First-time setup: create an account to begin.";
            FeedbackText.Foreground = System.Windows.Media.Brushes.LightGreen;
        }

        if (_registrationOnly)
        {
            ModeSwitchButton.Visibility = Visibility.Collapsed;
        }
    }

    private void SetMode(bool registerMode)
    {
        _isRegisterMode = registerMode;
        LoginFormPanel.Visibility = registerMode ? Visibility.Collapsed : Visibility.Visible;
        RegisterFormPanel.Visibility = registerMode ? Visibility.Visible : Visibility.Collapsed;
        HeadingText.Text = registerMode ? "Create your account" : "Welcome back";
        SubheadingText.Text = registerMode
            ? "Create a workspace account with a unique username and password."
            : "Sign in to continue to your incident workspace.";

        if (!_registrationOnly)
        {
            ModeSwitchButton.Content = registerMode
                ? "Already registered? Sign in"
                : "New here? Create an account";
        }

        FeedbackText.Text = string.Empty;
        FeedbackText.Foreground = System.Windows.Media.Brushes.LightCoral;
    }

    private void ModeSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        SetMode(!_isRegisterMode);
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_registrationOnly)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(LoginUsernameTextBox.Text) ||
            string.IsNullOrEmpty(LoginPasswordBox.Password))
        {
            FeedbackText.Text = "Enter both your username and password.";
            return;
        }

        try
        {
            if (_accountStore.TryAuthenticate(
                    LoginUsernameTextBox.Text,
                    LoginPasswordBox.Password,
                    out var user) &&
                user is not null)
            {
                AuthenticatedUser = user;
                DialogResult = true;
                Close();
                return;
            }

            FeedbackText.Text = "Username or password is incorrect.";
            LoginPasswordBox.Clear();
        }
        catch (Exception exception)
        {
            FeedbackText.Text = $"Account storage is unavailable: {exception.Message}";
        }
    }

    private void RegisterButton_Click(object sender, RoutedEventArgs e)
    {
        var displayName = RegisterDisplayNameTextBox.Text;
        var username = RegisterUsernameTextBox.Text;
        var password = RegisterPasswordBox.Password;

        if (!string.Equals(password, ConfirmPasswordBox.Password, StringComparison.Ordinal))
        {
            FeedbackText.Text = "The passwords do not match.";
            return;
        }

        try
        {
            if (!_accountStore.TryCreateAccount(
                    displayName,
                    username,
                    password,
                    out var user,
                    out var error) ||
                user is null)
            {
                FeedbackText.Text = error;
                return;
            }

            if (_registrationOnly)
            {
                MessageBox.Show(
                    this,
                    $"Account @{user.Username} was created successfully. Your current session is unchanged.",
                    "Account created",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                DialogResult = true;
                Close();
                return;
            }

            AuthenticatedUser = user;
            DialogResult = true;
            Close();
        }
        catch (Exception exception)
        {
            FeedbackText.Text = $"Account creation failed: {exception.Message}";
        }
    }

    private void LoginPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            LoginButton_Click(sender, e);
            e.Handled = true;
        }
    }
}
