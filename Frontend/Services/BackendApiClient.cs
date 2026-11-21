using System.Net.Http;
using System.IO;
using System.Net.Http.Json;
using System.Text.Json;
using AlertingSystem.Contracts;

namespace AlertingSystem.Services;

public static class BackendApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var address = Environment.GetEnvironmentVariable("ALERTINGSYSTEM_API_URL")
            ?? "http://127.0.0.1:5078/";

        if (!address.EndsWith("/", StringComparison.Ordinal))
            address += "/";

        return new HttpClient
        {
            BaseAddress = new Uri(address, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    public static void CheckHealth() => Get<JsonElement>("health");

    public static T Get<T>(string path)
    {
        using var response = Client.GetAsync(path).GetAwaiter().GetResult();
        return ReadResponse<T>(response);
    }

    public static T Post<T>(string path, object value)
    {
        using var response = Client.PostAsJsonAsync(path, value, JsonOptions).GetAwaiter().GetResult();
        return ReadResponse<T>(response);
    }

    public static T Put<T>(string path, object value)
    {
        using var response = Client.PutAsJsonAsync(path, value, JsonOptions).GetAwaiter().GetResult();
        return ReadResponse<T>(response);
    }

    private static T ReadResponse<T>(HttpResponseMessage response)
    {
        var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Backend returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {content}",
                null,
                response.StatusCode);
        }

        var result = JsonSerializer.Deserialize<T>(content, JsonOptions);
        return result ?? throw new InvalidDataException("Backend returned an empty or invalid response.");
    }
}
