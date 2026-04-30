using System;
using System.IO;
using Npgsql;

namespace Heimdall.DAL.Configuration;

/// <summary>
/// Helpers that translate Render-style URLs (e.g. <c>postgres://user:pass@host:port/db</c>) into
/// the formats expected by Npgsql and StackExchange.Redis.
/// </summary>
public static class ConnectionStringTranslator
{
    /// <summary>
    /// Converts a <c>postgres://</c> or <c>postgresql://</c> URL into an Npgsql connection string.
    /// If the input is already a key/value Npgsql connection string, it is returned unchanged.
    /// Returns <c>null</c> if <paramref name="value"/> is null/empty or the URL is malformed.
    /// </summary>
    /// <param name="value">URL or Npgsql connection string.</param>
    /// <returns>An Npgsql-compatible connection string, or <c>null</c> if <paramref name="value"/> is null/empty/malformed.</returns>
    public static string? ToNpgsqlConnectionString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (
            !value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
        )
        {
            // Assume it is already a key/value Npgsql connection string.
            return value;
        }

        Uri uri;
        try
        {
            uri = new Uri(value);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            return null;
        }

        var userInfo = uri.UserInfo.Split(':', 2);
        string username;
        string password;
        try
        {
            username = Uri.UnescapeDataString(userInfo[0]);
            password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            username = userInfo[0];
            password = userInfo.Length > 1 ? userInfo[1] : string.Empty;
        }

        var database = uri.AbsolutePath.TrimStart('/');

        var sslMode = ParseSslModeFromQuery(uri.Query);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = username,
            Password = password,
            Database = database,
            SslMode = sslMode,
        };

        var sslRootCert = ParseQueryParam(uri.Query, "sslrootcert");
        if (!string.IsNullOrEmpty(sslRootCert) && IsValidFilePath(sslRootCert))
        {
            builder.RootCertificate = sslRootCert;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Converts a <c>redis://</c> or <c>rediss://</c> URL into a StackExchange.Redis configuration string.
    /// If the input is already a host:port style config, it is returned unchanged.
    /// </summary>
    /// <param name="value">URL or StackExchange.Redis config string.</param>
    /// <returns>A StackExchange.Redis compatible configuration string, or <c>null</c> if <paramref name="value"/> is null/empty.</returns>
    public static string? ToRedisConfiguration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var isRediss = value.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase);
        var isRedis = value.StartsWith("redis://", StringComparison.OrdinalIgnoreCase);

        if (!isRedis && !isRediss)
        {
            return value;
        }

        var uri = new Uri(value);
        var host = uri.Host;
        var port = uri.IsDefaultPort ? 6379 : uri.Port;
        var config = $"{host}:{port}";

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            var password =
                parts.Length > 1
                    ? Uri.UnescapeDataString(parts[1])
                    : Uri.UnescapeDataString(parts[0]);
            if (!string.IsNullOrEmpty(password))
            {
                config += $",password={password}";
            }
        }

        if (isRediss)
        {
            config += ",ssl=true";
        }

        return config;
    }

    /// <summary>
    /// Parses the <c>sslmode</c> query parameter from a postgres URL query string and maps it to
    /// the corresponding <see cref="SslMode"/>. Returns <see cref="SslMode.Prefer"/> when the
    /// parameter is absent or unrecognised.
    /// </summary>
    private static SslMode ParseSslModeFromQuery(string? query)
    {
        var raw = ParseQueryParam(query, "sslmode");
        return raw?.ToLowerInvariant() switch
        {
            "require" => SslMode.Require,
            "verify-ca" => SslMode.VerifyCA,
            "verify-full" => SslMode.VerifyFull,
            "disable" => SslMode.Disable,
            "allow" => SslMode.Allow,
            _ => SslMode.Prefer,
        };
    }

    /// <summary>
    /// Returns the URL-decoded value of the first occurrence of <paramref name="key"/> in the
    /// given query string, or <c>null</c> if not found. If the value contains malformed
    /// percent-encoding sequences (e.g. <c>%GG</c>), the raw (undecoded) value is returned
    /// rather than throwing.
    /// </summary>
    private static string? ParseQueryParam(string? query, string key)
    {
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        var span = query.AsSpan().TrimStart('?');
        foreach (var segment in span.Split('&'))
        {
            var part = span[segment];
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            var paramKey = part[..eq];
            if (paramKey.Equals(key.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                var rawValue = part[(eq + 1)..].ToString();
                try
                {
                    return Uri.UnescapeDataString(rawValue);
                }
                catch (Exception ex) when (ex is UriFormatException or ArgumentException)
                {
                    return rawValue;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="path"/> is a rooted file-system path that refers
    /// to an existing file, providing a meaningful guard before assigning an
    /// externally-sourced value to
    /// <see cref="NpgsqlConnectionStringBuilder.RootCertificate"/>.
    /// </summary>
    private static bool IsValidFilePath(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && File.Exists(path);
    }
}
