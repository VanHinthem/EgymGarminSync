using Garmin.Connect.Auth;

namespace EgymGarminSync.Garmin.Cli;

/// <summary>
/// Opt-in safe trace of cache interactions. Does not log or expose token contents.
/// </summary>
internal sealed class GarminAuthTraceTokenCache(ITokenCache inner) : ITokenCache
{
    private int _get1, _get2, _set1, _set2;
    public string EventSummary => $"getOAuth1={_get1},getOAuth2={_get2},setOAuth1={_set1},setOAuth2={_set2}";

    public async Task<OAuth1Token?> GetOAuth1Token(CancellationToken ct)
    {
        Interlocked.Increment(ref _get1);
        return await inner.GetOAuth1Token(ct);
    }

    public async Task SetOAuth1Token(OAuth1Token token, CancellationToken ct)
    {
        Interlocked.Increment(ref _set1);
        await inner.SetOAuth1Token(token, ct);
    }

    public async Task<OAuth2Token?> GetOAuth2Token(CancellationToken ct)
    {
        Interlocked.Increment(ref _get2);
        return await inner.GetOAuth2Token(ct);
    }

    public async Task SetOAuth2Token(OAuth2Token token, CancellationToken ct)
    {
        Interlocked.Increment(ref _set2);
        await inner.SetOAuth2Token(token, ct);
    }
}
