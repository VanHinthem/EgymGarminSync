using System.Net;
using System.Text;
using EgymGarminSync.Egym;
using Xunit;

namespace EgymGarminSync.Egym.Tests;

public class EgymClientTests
{
    [Fact]
    public async Task GetWorkouts_UsesThirtyDayWindowsAndInclusiveEnd()
    {
        var requests = new List<Uri>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/np/exerciser/login" => Json("""{"uuid":"ex-1","homeClubUuid":"gym-1","timezone":"Europe/Amsterdam"}"""),
                "/np/micro-web-app/v1.0/exercisers/ex-1/tokens/FLS" => Json("""{"accessToken":"test-token","accessTokenExpiresAt":"2099-01-01T00:00:00Z"}"""),
                "/mwa/api/workouts/v1.0/workouts" => Json("""[{"completedAt":"2026-08-31T08:18:57Z","exercises":[]}]"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        using var client = new EgymClient("https://gym.example.com", "member@example.com", "test", http);

        var workouts = await client.GetWorkoutsAsync(new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 10));

        Assert.Equal(2, workouts.Count);
        var queryUrls = requests.Where(r => r.AbsolutePath.EndsWith("/workouts/v1.0/workouts")).ToArray();
        Assert.Equal(2, queryUrls.Length);
        Assert.All(queryUrls, r => Assert.Contains("gymLocationId=gym-1", r.Query));
        Assert.All(queryUrls, r => Assert.Contains("measurementSystem=METRIC", r.Query));
        Assert.True(queryUrls[0].Query.Contains("completedAfter=", StringComparison.Ordinal));
        Assert.True(queryUrls[1].Query.Contains("completedBefore=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetStrengthMeasurements_ReturnsOriginalJsonStructure()
    {
        using var http = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/np/exerciser/login" => Json("""{"uuid":"ex-1","homeClubUuid":"gym-1","timezone":"Europe/Amsterdam"}"""),
            "/np/micro-web-app/v1.0/exercisers/ex-1/tokens/FLS" => Json("""{"accessToken":"test-token","accessTokenExpiresAt":"2099-01-01T00:00:00Z"}"""),
            "/mwa/api/measurements/v1.0/strength" => Json("""{"strengthMeasurements":[{"id":"sample"}]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        using var client = new EgymClient("https://gym.example.com", "member@example.com", "test", http);

        var result = await client.GetStrengthMeasurementsAsync(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));
        Assert.Equal(1, result.GetProperty("strengthMeasurements").GetArrayLength());
    }

    [Fact]
    public async Task InvalidDateRange_DoesNotSendRequests()
    {
        var count = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            count++;
            return Json("{}");
        }));
        using var client = new EgymClient("https://gym.example.com", "member@example.com", "test", http);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetWorkoutsAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 8, 1)));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetStrengthMeasurementsAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 8, 1)));
        Assert.Equal(0, count);
    }

    [Fact]
    public void HttpGymBaseUrl_IsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => new EgymClient("http://gym.example.com", "member@example.com", "test"));
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }
}
