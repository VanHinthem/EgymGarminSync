using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Garmin.Connect.Auth;

namespace EgymGarminSync.Garmin.Cli;

/// <summary>Per-Windows-user encrypted OAuth cache. Never persists passwords or plaintext tokens.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsGarminTokenCache : ITokenCache
{
    private readonly string _path;
    private readonly byte[] _entropy;
    private readonly string _cooldownPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TokenDocument _tokens = new();

    public WindowsGarminTokenCache(string accountEmail)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Encrypted Garmin token caching currently requires Windows DPAPI.");

        var accountId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountEmail.Trim().ToLowerInvariant())));
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EgymGarminSync", "Garmin");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, accountId + ".dpapi");
        _cooldownPath = Path.Combine(folder, accountId + ".cooldown");
        _entropy = SHA256.HashData(Encoding.UTF8.GetBytes("EgymGarminSync:Garmin:OAuth:v1:" + accountId));
        Load();
    }

    public bool IsInLoginCooldown(out DateTimeOffset retryAfterUtc)
    {
        retryAfterUtc = default;
        if (!File.Exists(_cooldownPath)) return false;
        if (!long.TryParse(File.ReadAllText(_cooldownPath), out var ticks)) return false;
        retryAfterUtc = new DateTimeOffset(ticks, TimeSpan.Zero);
        return retryAfterUtc > DateTimeOffset.UtcNow;
    }

    public void RecordLoginFailure() => File.WriteAllText(_cooldownPath, DateTimeOffset.UtcNow.AddMinutes(30).UtcTicks.ToString());

    public void ClearLoginCooldown()
    {
        if (File.Exists(_cooldownPath)) File.Delete(_cooldownPath);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var encrypted = File.ReadAllBytes(_path);
            var json = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser);
            _tokens = JsonSerializer.Deserialize<TokenDocument>(json) ?? new TokenDocument();
            CryptographicOperations.ZeroMemory(json);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            // Do not silently overwrite an unreadable cache: it may be a permissions or account change.
            throw new InvalidOperationException("Cannot read protected Garmin token cache. Check Windows user and file permissions; do not share the cache.", ex);
        }
    }

    public async Task<OAuth2Token?> GetOAuth2Token(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Keep a 60-second safety margin so short-lived tokens are not returned near expiry.
            return _tokens.OAuth2ExpiresAtUtc > DateTimeOffset.UtcNow.AddSeconds(60) ? _tokens.OAuth2 : null;
        }
        finally { _gate.Release(); }
    }

    public async Task SetOAuth2Token(OAuth2Token token, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _tokens.OAuth2 = token;
            _tokens.OAuth2ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, token.ExpiresIn));
            Save();
        }
        finally { _gate.Release(); }
    }

    public async Task<OAuth1Token?> GetOAuth1Token(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return _tokens.OAuth1; }
        finally { _gate.Release(); }
    }

    public async Task SetOAuth1Token(OAuth1Token token, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _tokens.OAuth1 = token;
            Save();
        }
        finally { _gate.Release(); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void Save()
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(_tokens);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(clear, _entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(clear); }

        // Same-volume replace avoids a partially written cache after a crash.
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private sealed class TokenDocument
    {
        public OAuth1Token? OAuth1 { get; set; }
        public OAuth2Token? OAuth2 { get; set; }
        public DateTimeOffset OAuth2ExpiresAtUtc { get; set; }
    }
}
