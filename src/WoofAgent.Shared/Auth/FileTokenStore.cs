using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoofAgent.Shared.Auth;

public sealed record StoredToken(string AccessToken, DateTimeOffset? ExpiresAt)
{
    public TimeSpan? Remaining => ExpiresAt - DateTimeOffset.UtcNow;
    public bool IsExpired => ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow;
}

/// <summary>
/// Reads an OAuth access token from a JSON file written by scripts/swiggy_token.py.
/// The file is re-read whenever it changes, so a re-login is picked up by a running
/// process without a restart. Falls back to a static token (e.g. from user-secrets).
/// </summary>
public sealed class FileTokenStore
{
    private readonly string _path;
    private readonly string? _fallbackToken;
    private readonly object _lock = new();
    private DateTime _lastWriteUtc;
    private StoredToken? _cached;

    public FileTokenStore(string path, string? fallbackToken)
    {
        _path = ExpandHome(path);
        _fallbackToken = fallbackToken;
    }

    public string FilePath => _path;

    public StoredToken? GetCurrent()
    {
        lock (_lock)
        {
            if (File.Exists(_path))
            {
                var writeTime = File.GetLastWriteTimeUtc(_path);
                if (_cached is null || writeTime != _lastWriteUtc)
                {
                    var loaded = TryLoad(_path);
                    if (loaded is not null)
                    {
                        _cached = loaded;
                        _lastWriteUtc = writeTime;
                    }
                }

                if (_cached is not null)
                    return _cached;
            }

            return string.IsNullOrEmpty(_fallbackToken)
                ? null
                : new StoredToken(_fallbackToken, TryGetJwtExpiry(_fallbackToken));
        }
    }

    private static StoredToken? TryLoad(string path)
    {
        try
        {
            var file = JsonSerializer.Deserialize<TokenFile>(File.ReadAllText(path));
            if (string.IsNullOrEmpty(file?.AccessToken))
                return null;

            return new StoredToken(file.AccessToken, file.ExpiresAt ?? TryGetJwtExpiry(file.AccessToken));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // File is mid-write or malformed; keep using the previous token
            return null;
        }
    }

    private static DateTimeOffset? TryGetJwtExpiry(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
            return null;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string ExpandHome(string path) =>
        path.StartsWith("~/")
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;

    private sealed class TokenFile
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_at")]
        public DateTimeOffset? ExpiresAt { get; set; }
    }
}
