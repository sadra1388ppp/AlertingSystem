using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AlertingSystem.Models;
using AlertingSystem.Services;

namespace AlertingSystem;

public partial class LoginWindow : Window
{

    private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeWindowButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private readonly AccountStore _accountStore;
    private readonly SecurityAuditLogger _securityAuditLogger = new();
    private readonly bool _registrationOnly;
    private bool _isRegisterMode;
    private bool _loginInProgress;

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

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_registrationOnly || _loginInProgress)
        {
            return;
        }

        _loginInProgress = true;
        LoginButton.Content = "Signing in…";
        LoginButton.Background = (System.Windows.Media.Brush)FindResource("PrimaryHoverBrush");
        LoginButton.BorderBrush = (System.Windows.Media.Brush)FindResource("PrimaryHoverBrush");
        FeedbackText.Text = string.Empty;

        try
        {
            // Yield so the click animation and busy label can paint before work begins.
            await Task.Yield();

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

                // Keep the click feedback visible briefly even for an empty submission.
                await Task.Delay(160);
                return;
            }

            var username = LoginUsernameTextBox.Text;
            var password = LoginPasswordBox.Password;

            var authentication = await Task.Run(() =>
            {
                var isAuthenticated = _accountStore.TryAuthenticate(
                    username, password, out var authenticatedUser);
                return (IsAuthenticated: isAuthenticated, User: authenticatedUser);
            });

            if (authentication.IsAuthenticated && authentication.User is not null)
            {
                _securityAuditLogger.RecordSuccessfulLogin(authentication.User);
                AuthenticatedUser = authentication.User;
                DialogResult = true;
                Close();
                return;
            }

            _securityAuditLogger.RecordFailedLogin(
                username,
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
        finally
        {
            _loginInProgress = false;
            if (IsLoaded && AuthenticatedUser is null)
            {
                LoginButton.Content = "Sign in";
                LoginButton.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
                LoginButton.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
            }
        }
    }


    private void TextInputDirection_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            ApplyTextDirection(textBox, textBox.Text);
        }
    }

    private static void ApplyTextDirection(TextBox textBox, string? value)
    {
        var direction = ResolveTextDirection(value);
        textBox.FlowDirection = direction;
        textBox.TextAlignment = direction == FlowDirection.RightToLeft
            ? TextAlignment.Right
            : TextAlignment.Left;
    }

    private static FlowDirection ResolveTextDirection(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return FlowDirection.LeftToRight;
        }

        // Ignore prefixes, digits and punctuation; use the first letter to decide direction.
        foreach (var character in value)
        {
            if (!char.IsLetter(character))
            {
                continue;
            }

            return IsRightToLeftLetter(character)
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;
        }

        return FlowDirection.LeftToRight;
    }

    private static bool IsRightToLeftLetter(char character) =>
        (character >= '\u0600' && character <= '\u06FF') ||
        (character >= '\u0750' && character <= '\u077F') ||
        (character >= '\u08A0' && character <= '\u08FF') ||
        (character >= '\uFB50' && character <= '\uFDFF') ||
        (character >= '\uFE70' && character <= '\uFEFF');

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

    private void LoginUsernameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        SubmitLoginOnEnter(e);
    }

    private void LoginPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        SubmitLoginOnEnter(e);
    }

    private void SubmitLoginOnEnter(KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (_loginInProgress)
        {
            return;
        }

        // Raise the real button click so the press animation runs for Enter too.
        LoginButton.RaiseEvent(new RoutedEventArgs(
            System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
            LoginButton));
    }
}
