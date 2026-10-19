using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;

namespace AlertingSystem;

public partial class MainWindow : Window
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private int _checksRun;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void CheckEndpoint_Click(object sender, RoutedEventArgs e)
    {
        var address = EndpointTextBox.Text.Trim();

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            LastResultSummaryText.Text = "Invalid URL";
            ResponseTimeSummaryText.Text = "—";
            ResponseTimeText.Text = "Response time: —";
            SetStatus("Invalid URL", "DangerBrush");
            LastResultText.Text = "Enter a valid URL beginning with http:// or https://.";
            AddHistory("Invalid URL entered");
            return;
        }

        CheckEndpointButton.IsEnabled = false;
        CheckEndpointButton.Content = "Checking…";
        SetStatus("Checking…", "NeutralBrush");
        LastResultText.Text = $"Sending a request to {uri.Host}…";
        ResponseTimeText.Text = "Response time: checking…";

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await HttpClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead);

            stopwatch.Stop();

            var statusCode = (int)response.StatusCode;
            var isHealthy = response.IsSuccessStatusCode;
            var elapsed = stopwatch.ElapsedMilliseconds;

            SetStatus(isHealthy ? "Healthy" : $"HTTP {statusCode}",
                isHealthy ? "SuccessBrush" : "DangerBrush");

            LastResultText.Text =
                $"{uri.Host} returned HTTP {statusCode} ({response.ReasonPhrase}).";
            ResponseTimeText.Text = $"Response time: {elapsed} ms";
            ResponseTimeSummaryText.Text = $"{elapsed} ms";
            LastResultSummaryText.Text = isHealthy ? "Healthy" : $"HTTP {statusCode}";

            AddHistory($"{uri.Host}  •  HTTP {statusCode}  •  {elapsed} ms");
        }
        catch (TaskCanceledException)
        {
            stopwatch.Stop();
            SetStatus("Timed out", "DangerBrush");
            LastResultText.Text = $"{uri.Host} did not respond within 10 seconds.";
            ResponseTimeText.Text = "Response time: timed out";
            ResponseTimeSummaryText.Text = "Timeout";
            LastResultSummaryText.Text = "Timed out";
            AddHistory($"{uri.Host}  •  Timed out");
        }
        catch (HttpRequestException)
        {
            stopwatch.Stop();
            SetStatus("Unreachable", "DangerBrush");
            LastResultText.Text =
                $"Could not connect to {uri.Host}. Check the URL and your network connection.";
            ResponseTimeText.Text = "Response time: —";
            ResponseTimeSummaryText.Text = "—";
            LastResultSummaryText.Text = "Unreachable";
            AddHistory($"{uri.Host}  •  Unreachable");
        }
        finally
        {
            _checksRun++;
            ChecksRunText.Text = _checksRun.ToString();

            if (HistoryList.Items.Count > 10)
            {
                HistoryList.Items.RemoveAt(10);
            }

            CheckEndpointButton.IsEnabled = true;
            CheckEndpointButton.Content = "Check endpoint";
        }
    }

    private void SetStatus(string message, string brushKey)
    {
        CheckStatusText.Text = message;
        StatusIndicator.Fill = (Brush)FindResource(brushKey);
    }

    private void AddHistory(string message)
    {
        HistoryList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  •  {message}");

        if (HistoryList.Items.Count > 10)
        {
            HistoryList.Items.RemoveAt(10);
        }
    }
}
