using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Heimdall.Web.RateLimiting;

/// <summary>
/// Constants shared between <see cref="RateLimitFormEmailMiddleware"/> and the
/// rate-limiter policy callbacks in <c>Program.cs</c>.
/// </summary>
internal static class RateLimitFormEmailKeys
{
    /// <summary>
    /// <see cref="HttpContext.Items"/> key under which the form-submitted
    /// <c>email</c> value is stashed for the (ip|email)-keyed rate limiter.
    /// </summary>
    internal const string SubmittedEmailItemKey = "Heimdall.RateLimit.SubmittedEmail";
}

/// <summary>
/// Tiny middleware that asynchronously reads the submitted email on the small
/// set of POST endpoints whose rate-limit policy keys on
/// <c>(ip|submitted-email)</c> and stashes it into
/// <see cref="HttpContext.Items"/> for the policy callback to read
/// synchronously. Supports both form posts and JSON API bodies so the same
/// rate-limit key shape applies to account-form and API auth flows.
/// </summary>
/// <remarks>
/// Scoped narrowly: only POSTs on known account/API auth routes are examined,
/// so general request handling is untouched.
/// </remarks>
internal static class RateLimitFormEmailMiddleware
{
    private static readonly string[] FormTargetPaths =
    {
        "/account/login",
        "/account/forgot-password",
        "/account/reset-password",
    };
    private static readonly string[] JsonTargetPaths =
    {
        "/api/v1/auth/register",
        "/api/v1/auth/forgot-password",
        "/api/v1/auth/reset-password",
    };

    /// <summary>
    /// Registers the form-email pre-read middleware. Must run BEFORE
    /// <c>app.UseRateLimiter()</c> so the policy callback observes the value.
    /// </summary>
    public static IApplicationBuilder UseRateLimitFormEmailExtraction(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(static async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method)
                && context.Request.HasFormContentType
                && MatchesTargetPath(context.Request.Path, FormTargetPaths))
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted)
                    .ConfigureAwait(false);
                context.Items[RateLimitFormEmailKeys.SubmittedEmailItemKey] =
                    form["email"].ToString();
            }
            else if (HttpMethods.IsPost(context.Request.Method)
                && IsJsonContentType(context.Request.ContentType)
                && MatchesTargetPath(context.Request.Path, JsonTargetPaths))
            {
                context.Request.EnableBuffering();
                try
                {
                    using var document = await JsonDocument.ParseAsync(
                        context.Request.Body,
                        cancellationToken: context.RequestAborted).ConfigureAwait(false);

                    if (TryGetEmail(document.RootElement, out string? email)
                        && !string.IsNullOrWhiteSpace(email))
                    {
                        context.Items[RateLimitFormEmailKeys.SubmittedEmailItemKey] = email;
                    }
                }
                catch (JsonException)
                {
                    // Invalid JSON payloads are handled by endpoint model binding;
                    // rate limiting should still proceed on the IP partition.
                }
                finally
                {
                    if (context.Request.Body.CanSeek)
                    {
                        context.Request.Body.Seek(0, SeekOrigin.Begin);
                    }
                }
            }

            await next().ConfigureAwait(false);
        });
    }

    private static bool MatchesTargetPath(PathString path, string[] targetPaths)
    {
        return targetPaths.Any(prefix =>
            path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsJsonContentType(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType)
        && contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetEmail(JsonElement rootElement, out string? email)
    {
        if (rootElement.ValueKind != JsonValueKind.Object)
        {
            email = null;
            return false;
        }

        foreach (JsonProperty property in rootElement.EnumerateObject())
        {
            if (!string.Equals(property.Name, "email", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            email = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : null;
            return true;
        }

        email = null;
        return false;
    }
}
