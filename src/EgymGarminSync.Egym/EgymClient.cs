using Claunia.PropertyList;
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
    private Uri? _gymUrl;
    private readonly string _email;
    private readonly string _password;
    private string? _exerciserId;
    private string? _gymId;
    private string? _timeZone;
    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public EgymClient(string? gymBaseUrl, string email, string password, HttpClient? httpClient = null)
    {
        if (!string.IsNullOrWhiteSpace(gymBaseUrl)) _gymUrl = NormalizeBaseUrl(ValidateHttpsUrl(gymBaseUrl));
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

    private static Uri ValidateHttpsUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Discovery returned an invalid HTTPS URL.");
        return uri;
    }

    // The Branding.plist download link may contain signed query parameters.
    // Keep these intact; only the actual gym backend must be a plain base URL.
    private static Uri ValidateBrandingResourceUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Discovery returned an invalid Branding.plist resource URL.");
        return uri;
    }

    private static Uri NormalizeBaseUrl(Uri uri) => new(uri.ToString().TrimEnd('/') + "/");

    private Uri GymUri(string path) => new(_gymUrl ?? throw new InvalidOperationException("Gym discovery has not completed."), path.TrimStart('/'));

    /// <summary>Discover Netpulse gym backend via official EGYM app discovery endpoints.</summary>
    public async Task<Uri> DiscoverGymAsync(CancellationToken ct = default)
    {
        if (_gymUrl is not null) return _gymUrl;
        using var users = await _http.GetAsync("https://one.netpulse.com/np/egym/v1.0/users?email=" + Uri.EscapeDataString(_email), ct);
        await EnsureSuccessAsync(users);
        using var userJson = JsonDocument.Parse(await users.Content.ReadAsStreamAsync(ct));
        var container = userJson.RootElement.GetProperty("containerData");
        var brand = container.GetProperty("brandIdentifier").GetString();
        var type = container.GetProperty("resourceType").GetString();
        if (string.IsNullOrWhiteSpace(brand) || string.IsNullOrWhiteSpace(type))
            throw new InvalidOperationException("No EGYM gym association was returned.");
        var configUrl = "https://one.netpulse.com/np/nfa/config?brandIdentifier=" + Uri.EscapeDataString(brand) +
            "&resourceType=" + Uri.EscapeDataString(type);
        using var config = await _http.GetAsync(configUrl, ct);
        await EnsureSuccessAsync(config);
        using var configJson = JsonDocument.Parse(await config.Content.ReadAsStreamAsync(ct));
        string? plistUrl = null;
        foreach (var resource in configJson.RootElement.GetProperty("resources").EnumerateArray())
        {
            if (resource.TryGetProperty("key", out var key) && key.GetString() == "Branding.plist")
            {
                plistUrl = resource.GetProperty("url").GetString();
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(plistUrl)) throw new InvalidOperationException("No Branding.plist in gym configuration.");
        // Reject insecure or embedded-credential URLs before following the discovery-provided resource.
        var brandingUri = ValidateBrandingResourceUrl(plistUrl);
        using var branding = await _http.GetAsync(brandingUri, ct);
        await EnsureSuccessAsync(branding);
        var bytes = await branding.Content.ReadAsByteArrayAsync(ct);
        if (PropertyListParser.Parse(bytes) is not NSDictionary root ||
            root.ObjectForKey("NetworkSettings") is not NSDictionary network ||
            network.ObjectForKey("NGBackendAddressKey") is not NSString address)
            throw new InvalidOperationException("Branding.plist does not contain a Netpulse backend URL.");
        _gymUrl = NormalizeBaseUrl(ValidateHttpsUrl(address.ToString()));
        return _gymUrl;
    }

    public async Task LoginAsync(CancellationToken ct = default)
    {
        if (_gymUrl is null) await DiscoverGymAsync(ct);
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
