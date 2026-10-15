using System.Net;

namespace EgymGarminSync.Web;

/// <summary>
/// Transport-level fail-closed guard: at most one exerciseSets PUT reaches the
/// inner HTTP handler. Authentication retries may occur above this layer.
/// This handler must be scoped to one write attempt, never shared across writes.
/// </summary>
internal sealed class GarminSinglePutGuard(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private int _attempts;
    internal int PutAttempts => Volatile.Read(ref _attempts);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,CancellationToken cancellationToken)
    {
        if(request.Method==HttpMethod.Put &&
            request.RequestUri?.AbsolutePath.EndsWith("/exerciseSets",StringComparison.OrdinalIgnoreCase)==true)
        {
            if(Interlocked.Increment(ref _attempts)!=1)
                throw new InvalidOperationException("Blocked repeated Garmin exerciseSets PUT.");
        }
        return base.SendAsync(request,cancellationToken);
    }
}
