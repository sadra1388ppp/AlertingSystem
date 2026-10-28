using System.Windows;

namespace AlertingSystem;

public partial class DeleteAccountWindow : Window
{
    public string ConfirmedPassword { get; private set; } = string.Empty;

    public DeleteAccountWindow(string username)
    {
        InitializeComponent();
        AccountUsernameText.Text = "@" + username;
    }

    private void ConfirmDeletePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        FeedbackText.Text = string.Empty;
    }

    private void ConfirmDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ConfirmDeletePasswordBox.Password))
        {
            FeedbackText.Text = "Enter your current password to confirm this action.";
            return;
        }

        ConfirmedPassword = ConfirmDeletePasswordBox.Password;
        DialogResult = true;
    }
}
