using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EgymGarminSync.Garmin.Cli;
using Garmin.Connect.Auth;

namespace EgymGarminSync.Web;

// Windows uses the original user-scoped DPAPI cache; Linux uses AES-256-GCM
// with a separately provided key. No plaintext tokens are persisted.
internal sealed class GarminTokenCacheProvider : ITokenCache
{
    private readonly WindowsGarminTokenCache? windows;
    private readonly LinuxEncryptedTokenCache? linux;

    internal GarminTokenCacheProvider(string email)
    {
        if (OperatingSystem.IsWindows()) windows = new WindowsGarminTokenCache(email);
        else if (OperatingSystem.IsLinux()) linux = new LinuxEncryptedTokenCache(email);
        else throw new PlatformNotSupportedException("Unsupported Garmin token cache platform.");
    }

    internal static bool IsSupported =>
        OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && LinuxEncryptedTokenCache.HasValidKey);

    public Task<OAuth2Token?> GetOAuth2Token(CancellationToken ct) =>
        windows?.GetOAuth2Token(ct) ?? linux!.GetOAuth2Token(ct);
    public Task SetOAuth2Token(OAuth2Token token,CancellationToken ct) =>
        windows?.SetOAuth2Token(token,ct) ?? linux!.SetOAuth2Token(token,ct);
    public Task<OAuth1Token?> GetOAuth1Token(CancellationToken ct) =>
        windows?.GetOAuth1Token(ct) ?? linux!.GetOAuth1Token(ct);
    public Task SetOAuth1Token(OAuth1Token token,CancellationToken ct) =>
        windows?.SetOAuth1Token(token,ct) ?? linux!.SetOAuth1Token(token,ct);
    internal bool IsInLoginCooldown(out DateTimeOffset retryAfterUtc)
    {
        if(windows is not null) return windows.IsInLoginCooldown(out retryAfterUtc);
        return linux!.IsInLoginCooldown(out retryAfterUtc);
    }
    internal void ClearLoginCooldown()
    {
        if(windows is not null) windows.ClearLoginCooldown();
        else linux!.ClearLoginCooldown();
    }
    internal void RecordLoginFailure()
    {
        if(windows is not null) windows.RecordLoginFailure();
        else linux!.RecordLoginFailure();
    }
}

internal sealed class LinuxEncryptedTokenCache : ITokenCache
{
    private sealed class Tokens
    {
        public OAuth1Token? OAuth1 {get;set;}
        public OAuth2Token? OAuth2 {get;set;}
        public DateTimeOffset ExpiresAt {get;set;}
    }

    private readonly string path;
    private readonly string cooldownPath;
    private readonly byte[] key;
    private readonly SemaphoreSlim gate=new(1,1);
    private Tokens tokens=new();

    internal static bool HasValidKey
    {
        get
        {
            try { return Convert.FromBase64String(Environment.GetEnvironmentVariable("GARMIN_TOKEN_KEY") ?? "").Length==32; }
            catch(FormatException) { return false; }
        }
    }

    internal LinuxEncryptedTokenCache(string email)
    {
        if(!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        key=Convert.FromBase64String(Environment.GetEnvironmentVariable("GARMIN_TOKEN_KEY") ??
            throw new InvalidOperationException("GARMIN_TOKEN_KEY is required on Linux."));
        if(key.Length!=32) throw new InvalidOperationException("GARMIN_TOKEN_KEY must contain a base64-encoded 32-byte key.");
        var accountId=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())));
        var folder=Path.Combine(Environment.GetEnvironmentVariable("EGYM_SYNC_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EgymGarminSync"),"Garmin");
        Directory.CreateDirectory(folder);
        try { File.SetUnixFileMode(folder,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute); } catch(UnauthorizedAccessException) { throw; }
        path=Path.Combine(folder,accountId+".aesgcm");
        cooldownPath=Path.Combine(folder,accountId+".cooldown");
        if(File.Exists(path))
        {
            var encrypted=File.ReadAllBytes(path);
            if(encrypted.Length<29||encrypted[0]!=1)throw new InvalidOperationException("Invalid encrypted Garmin token cache.");
            var nonce=encrypted.AsSpan(1,12);
            var tag=encrypted.AsSpan(13,16);
            var plain=new byte[encrypted.Length-29];
            try
            {
                using var aes=new AesGcm(key,16);
                aes.Decrypt(nonce,encrypted.AsSpan(29),tag,plain,Encoding.UTF8.GetBytes(accountId));
                tokens=JsonSerializer.Deserialize<Tokens>(plain) ?? throw new InvalidOperationException("Invalid tokens.");
            }
            catch(CryptographicException ex) { throw new InvalidOperationException("Garmin token key mismatch; refusing to overwrite cache.",ex); }
            finally {CryptographicOperations.ZeroMemory(plain);}
        }
    }
    private void Save()
    {
        var plain=JsonSerializer.SerializeToUtf8Bytes(tokens);
        var encrypted=new byte[29+plain.Length];
        encrypted[0]=1;
        RandomNumberGenerator.Fill(encrypted.AsSpan(1,12));
        var accountId=Path.GetFileNameWithoutExtension(path);
        try
        {
            using var aes=new AesGcm(key,16);
            aes.Encrypt(encrypted.AsSpan(1,12),plain,encrypted.AsSpan(29),encrypted.AsSpan(13,16),Encoding.UTF8.GetBytes(accountId));
            var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    File.SetUnixFileMode(temp,UnixFileMode.UserRead|UnixFileMode.UserWrite);
                    stream.Write(encrypted);
                    stream.Flush(true);
                }
                File.Move(temp,path,true);
            }
            finally {if(File.Exists(temp))File.Delete(temp);}
        }
        finally {CryptographicOperations.ZeroMemory(plain);}
    }
    public async Task<OAuth2Token?> GetOAuth2Token(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try{return tokens.ExpiresAt>DateTimeOffset.UtcNow.AddSeconds(60)?tokens.OAuth2:null;}
        finally {gate.Release();}
    }
    public async Task SetOAuth2Token(OAuth2Token token,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try{tokens.OAuth2=token;tokens.ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(Math.Max(0,token.ExpiresIn));Save();}
        finally{gate.Release();}
    }
    public async Task<OAuth1Token?> GetOAuth1Token(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try{return tokens.OAuth1;}
        finally{gate.Release();}
    }
    public async Task SetOAuth1Token(OAuth1Token token,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try{tokens.OAuth1=token;Save();}
        finally{gate.Release();}
    }
    internal bool IsInLoginCooldown(out DateTimeOffset retryAfterUtc)
    {
        retryAfterUtc=default;
        if(!File.Exists(cooldownPath)||!long.TryParse(File.ReadAllText(cooldownPath),out var ticks))return false;
        retryAfterUtc=new DateTimeOffset(ticks,TimeSpan.Zero);
        return retryAfterUtc>DateTimeOffset.UtcNow;
    }
    internal void ClearLoginCooldown(){if(File.Exists(cooldownPath))File.Delete(cooldownPath);}
    internal void RecordLoginFailure(){File.WriteAllText(cooldownPath,DateTimeOffset.UtcNow.AddMinutes(30).UtcTicks.ToString());}
}
