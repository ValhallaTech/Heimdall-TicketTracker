using System;
using System.Linq;
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
/// Tiny middleware that asynchronously reads the request form on the small set
/// of POST endpoints whose rate-limit policy keys on <c>(ip|submitted-email)</c>
/// (login, forgot-password, reset-password) and stashes the submitted address
/// into <see cref="HttpContext.Items"/> for the policy callback to read
/// synchronously. This keeps the synchronous rate-limit partition callback
/// off <c>ReadFormAsync().GetAwaiter().GetResult()</c> — which would
/// sync-block the threadpool on body I/O if the form is not yet buffered —
/// per the Phase 1 PR review feedback.
/// </summary>
/// <remarks>
/// Scoped narrowly: only POSTs with a form content-type whose path starts with
/// one of the known account routes do the read, so general request handling
/// is untouched.
/// </remarks>
internal static class RateLimitFormEmailMiddleware
{
    private static readonly string[] TargetPaths =
    {
        "/account/login",
        "/account/forgot-password",
        "/account/reset-password",
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
                && MatchesTargetPath(context.Request.Path))
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted)
                    .ConfigureAwait(false);
                context.Items[RateLimitFormEmailKeys.SubmittedEmailItemKey] =
                    form["email"].ToString();
            }

            await next().ConfigureAwait(false);
        });
    }

    private static bool MatchesTargetPath(PathString path)
    {
        return TargetPaths.Any(prefix =>
            path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
