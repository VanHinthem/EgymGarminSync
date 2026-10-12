using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EgymGarminSync.Egym;

/// <summary>Read-only EGYM Fitness client. Requires the gym Netpulse base URL for this first slice.</summary>
public sealed class EgymClient : IDisposable
{
    private const string MwaBase = "https://mwa-api.int.api.egym.com/mwa/api";
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly Uri _gymUrl;
    private readonly string _email;
    private readonly string _password;
    private string? _exerciserId;
    private string? _gymId;
    private string? _timeZone;
    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public EgymClient(string gymBaseUrl, string email, string password, HttpClient? httpClient = null)
    {
        if (!Uri.TryCreate(gymBaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("A valid HTTPS gym Netpulse URL is required.", nameof(gymBaseUrl));
        _gymUrl = new Uri(uri.ToString().TrimEnd('/') + "/");
        _email = !string.IsNullOrWhiteSpace(email) ? email : throw new ArgumentException("Email required.", nameof(email));
        _password = !string.IsNullOrWhiteSpace(password) ? password : throw new ArgumentException("Password required.", nameof(password));
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-NP-API-Version", "1.5");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-NP-APP-Version", "3.91");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-GB");
        // EGYM's undocumented member endpoint identifies callers as the mobile fitness app.
        var deviceId = Guid.NewGuid().ToString().ToUpperInvariant();
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-NP-User-Agent",
            $"clientType=MOBILE_DEVICE; devicePlatform=IOS; deviceUid={deviceId}; applicationName=EGYM Fitness; applicationVersion=3.91; applicationVersionCode=1190; containerName=NetpulseFitness;");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NetpulseFitness/3.91");
    }

    private Uri GymUri(string path) => new(_gymUrl, path.TrimStart('/'));

    public async Task LoginAsync(CancellationToken ct = default)
    {
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = _email, ["password"] = _password
        });
        using var response = await _http.PostAsync(GymUri("np/exerciser/login"), body, ct);
        await EnsureSuccessAsync(response);
        using var login = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = login.RootElement;
        _exerciserId = root.GetProperty("uuid").GetString() ?? throw new JsonException("Missing exerciser id");
        _gymId = root.GetProperty("homeClubUuid").GetString() ?? throw new JsonException("Missing club id");
        _timeZone = root.GetProperty("timezone").GetString() ?? throw new JsonException("Missing timezone");
        _accessToken = null;
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-1))
            return _accessToken;
        if (_exerciserId is null) await LoginAsync(ct);
        var url = GymUri($"np/micro-web-app/v1.0/exercisers/{Uri.EscapeDataString(_exerciserId!)}/tokens/FLS");
        using var response = await _http.GetAsync(url, ct);
        await EnsureSuccessAsync(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        _accessToken = json.RootElement.GetProperty("accessToken").GetString()
            ?? throw new JsonException("Missing EGYM access token");
        _expiresAt = DateTimeOffset.Parse(json.RootElement.GetProperty("accessTokenExpiresAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        return _accessToken;
    }

    /// <summary>Retrieves all workout records in inclusive gym-local date range using 30-day request windows.</summary>
    public async Task<IReadOnlyList<JsonElement>> GetWorkoutsAsync(DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        if (end < start) throw new ArgumentException("End must be on or after start.");
        if (_gymId is null) await LoginAsync(ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(_timeZone!);
        var from = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(start.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var exclusive = end.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var until = new DateTimeOffset(exclusive, zone.GetUtcOffset(exclusive)).ToUniversalTime();
        var results = new List<JsonElement>();
        while (from < until)
        {
            var to = from.AddDays(30);
            if (to > until) to = until;
            var query = $"completedAfter={Uri.EscapeDataString(from.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}" +
                        $"&completedBefore={Uri.EscapeDataString(to.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}" +
                        $"&gymLocationId={Uri.EscapeDataString(_gymId!)}&measurementSystem=METRIC&locale=en-GB";
            var url = $"{MwaBase}/workouts/v1.0/workouts?{query}";
            using var response = await GetMwaAsync(url, ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected workout array.");
            foreach (var item in json.RootElement.EnumerateArray()) results.Add(item.Clone());
            from = to;
        }
        return results;
    }

    private async Task<HttpResponseMessage> GetMwaAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
            var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                _accessToken = null;
                continue;
            }
            await EnsureSuccessAsync(response);
            return response;
        }
        throw new InvalidOperationException("EGYM request could not be authorized.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var status = response.StatusCode;
        response.Dispose();
        await Task.CompletedTask;
        throw new HttpRequestException($"EGYM request failed with HTTP {(int)status}.", null, status);
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
