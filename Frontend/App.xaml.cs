using System.IO;
using System.Windows;
using System.Windows.Threading;
using AlertingSystem.Services;

namespace AlertingSystem;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            ShowApplicationError(args.Exception);
            args.Handled = true;
        };

        try
        {
            try
            {
                BackendApiClient.CheckHealth();
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    $"The AlertingSystem backend is not running or cannot be reached. Start it first with:\n\n" +
                    "dotnet run --project Backend/AlertingSystem.Backend.csproj\n\n" +
                    $"Details: {exception.Message}",
                    "Backend unavailable",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Shutdown(-1);
                return;
            }

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var loginWindow = new LoginWindow();
            var loginResult = loginWindow.ShowDialog();

            if (loginResult != true || loginWindow.AuthenticatedUser is null)
            {
                Shutdown();
                return;
            }

            var window = new MainWindow(loginWindow.AuthenticatedUser);
            MainWindow = window;
            window.Show();
            ShutdownMode = ShutdownMode.OnLastWindowClose;
        }
        catch (Exception exception)
        {
            ShowApplicationError(exception);
            Shutdown(-1);
        }
    }

    private static void ShowApplicationError(Exception exception)
    {
        var message = exception.ToString();

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AlertingSystem");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "startup-error.log"), message);
        }
        catch
        {
        }

        MessageBox.Show(
            message,
            "AlertingSystem could not start",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
