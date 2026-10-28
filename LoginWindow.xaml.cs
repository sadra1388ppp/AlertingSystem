using System.Windows;
using System.Windows.Input;
using AlertingSystem.Models;
using AlertingSystem.Services;

namespace AlertingSystem;

public partial class LoginWindow : Window
{
    private readonly AccountStore _accountStore;
    private readonly SecurityAuditLogger _securityAuditLogger = new();
    private readonly bool _registrationOnly;
    private bool _isRegisterMode;

    public AccountUser? AuthenticatedUser { get; private set; }

    public LoginWindow(bool openRegistration = false, bool registrationOnly = false)
    {
        InitializeComponent();
        _registrationOnly = registrationOnly;
        _accountStore = new AccountStore();

        var hasAccounts = _accountStore.HasAccounts();
        _securityAuditLogger.FlushPendingEvents();
        SetMode(openRegistration || !hasAccounts);

        if (!hasAccounts && !registrationOnly)
        {
            FeedbackText.Text = "First-time setup: create an account to begin.";
            FeedbackText.Foreground = System.Windows.Media.Brushes.LightGreen;
        }

        if (_registrationOnly || !hasAccounts)
        {
            ModeSwitchButton.Visibility = Visibility.Collapsed;
            ModeSwitchFooter.Visibility = Visibility.Collapsed;
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
            ModeSwitchPromptText.Text = registerMode
                ? "ALREADY HAVE AN ACCOUNT?"
                : "NEW TO ALERTINGSYSTEM?";
            ModeSwitchDescriptionText.Text = registerMode
                ? "Return to secure sign-in"
                : "Create your workspace account";
            ModeSwitchButton.Content = registerMode
                ? "Sign in instead  →"
                : "Create account  →";
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
            _securityAuditLogger.RecordFailedLogin(
                LoginUsernameTextBox.Text,
                "Incomplete credentials",
                "The sign-in form was submitted with a missing username or password. Credential validation was not attempted.",
                IncidentSeverity.Low);
            FeedbackText.Text = "Enter both your username and password.";
            LoginPasswordBox.Clear();
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
                _securityAuditLogger.RecordSuccessfulLogin(user);
                AuthenticatedUser = user;
                DialogResult = true;
                Close();
                return;
            }

            _securityAuditLogger.RecordFailedLogin(
                LoginUsernameTextBox.Text,
                "Invalid credentials",
                "The submitted username/password pair did not match a registered account. For privacy, the application does not disclose which field was incorrect.");
            FeedbackText.Text = "Username or password is incorrect.";
            LoginPasswordBox.Clear();
        }
        catch (Exception exception)
        {
            _securityAuditLogger.RecordFailedLogin(
                LoginUsernameTextBox.Text,
                "Authentication service error",
                $"Authentication could not complete because the account store raised {exception.GetType().Name}: {exception.Message}. No password was recorded.",
                IncidentSeverity.High);
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

            _securityAuditLogger.RecordAccountCreated(user);

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

            _securityAuditLogger.RecordSuccessfulLogin(
                user,
                "First sign-in completed immediately after account registration.");
            AuthenticatedUser = user;
            DialogResult = true;
            Close();
        }
        catch (Exception exception)
        {
            FeedbackText.Text = $"Account creation failed: {exception.Message}";
        }
    }

    private void RegisterInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        RegisterButton_Click(sender, e);
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
