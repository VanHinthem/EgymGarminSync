using System.Diagnostics;
using System.Net;

namespace EgymGarminSync.Garmin.Cli;

/// <summary>Opt-in request timing without paths, query strings, headers, or response bodies.</summary>
internal sealed class GarminHttpTraceHandler : DelegatingHandler
{
    public GarminHttpTraceHandler(HttpMessageHandler inner) : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host ?? "(unknown)";
        var method = request.Method.Method;
        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine($"HTTP START host={host} method={method}");
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            Console.WriteLine($"HTTP END host={host} method={method} status={(int)response.StatusCode} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return response;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HTTP FAILED host={host} method={method} error={ex.GetType().Name} elapsedMs={stopwatch.ElapsedMilliseconds}");
            throw;
        }
    }
}
