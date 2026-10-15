using Garmin.Connect;
using Garmin.Connect.Auth;

namespace EgymGarminSync.Web;

/// <summary>
/// Sole construction path for a future Garmin write context. One new guard and
/// HttpClient are created for each approved write attempt; never share them.
/// Does not authorize a write or open a connection by itself.
/// </summary>
internal static class GarminGuardedWriteContextFactory
{
    internal sealed class Scope : IDisposable
    {
        private readonly HttpClient _http;
        internal GarminSinglePutGuard Guard { get; }
        internal GarminConnectContext Context { get; }

        internal Scope(HttpMessageHandler inner, IAuthParameters auth,
            IMfaCodeProvider mfa, ITokenCache tokens)
        {
            Guard = new GarminSinglePutGuard(inner);
            _http = new HttpClient(Guard);
            Context = new GarminConnectContext(_http, auth, mfa, tokens);
        }

        public void Dispose() => _http.Dispose();
    }

    internal static Scope Create(HttpMessageHandler inner, IAuthParameters auth,
        IMfaCodeProvider mfa, ITokenCache tokens)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(mfa);
        ArgumentNullException.ThrowIfNull(tokens);
        return new Scope(inner, auth, mfa, tokens);
    }
}
