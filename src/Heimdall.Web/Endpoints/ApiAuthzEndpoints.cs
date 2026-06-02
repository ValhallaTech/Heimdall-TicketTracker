using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Web.Authorization.Policies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Heimdall.Web.Endpoints;

/// <summary>
/// Phase 6.2 step 7 (<c>docs/proposals/blazor-to-svelte-transition.md</c> §4.2) — the
/// <c>/api/v1/authz/check</c> permission-probe endpoint the SvelteKit frontend calls to
/// drive conditional UI (show/hide an edit button, gate a route, etc.) without taking a
/// dependency on the OpenFGA sidecar from the browser. Minimal API, JSON in / JSON out,
/// bearer-only — mirroring <see cref="ApiTicketsEndpoints"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Subject is server-derived, never client-supplied.</strong> The request body
/// carries only <c>relation</c> + <c>object</c>; the <c>user:</c> subject is built from
/// the authenticated principal's id claim (the same <c>sub</c> / <see cref="ClaimTypes.NameIdentifier"/>
/// claim <see cref="ApiTicketsEndpoints"/> uses) via <see cref="TupleShapes.UserRef(Guid)"/>.
/// A caller therefore cannot probe permissions <em>as</em> another user.
/// </para>
/// <para>
/// <strong>All Check() calls stay server-side.</strong> The endpoint resolves
/// <see cref="IOpenFgaAuthorizationService"/> from DI and issues the
/// <see cref="IOpenFgaAuthorizationService.CheckAsync(FgaCheckRequest, CancellationToken)"/>
/// itself — the service already deny-closes (returns <c>false</c>) on sidecar transport /
/// 5xx failure, so a UI that trusts <c>allowed:false</c> fails safe.
/// </para>
/// </remarks>
public static class ApiAuthzEndpoints
{
    private const string ProblemJsonContentType = "application/problem+json";

    /// <summary>
    /// Maps <c>POST /api/v1/authz/check</c> onto <paramref name="endpoints"/>. Called from
    /// <c>Program.cs</c> immediately after <see cref="ApiTicketsEndpoints.MapApiTicketsEndpoints"/>
    /// so it shares the same routing namespace + middleware pipeline as the rest of the
    /// <c>/api/v1/*</c> surface.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns><paramref name="endpoints"/> for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is <c>null</c>.</exception>
    public static IEndpointRouteBuilder MapApiAuthzEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints
            .MapGroup("/api/v1/authz")
            .RequireRateLimiting("api-token")
            .WithTags("Authz");

        // Bearer-only, authenticated user required — same gate the collection-shaped
        // ticket endpoints use. DisableAntiforgery because the app calls
        // app.UseAntiforgery(): bearer is this surface's CSRF defense, so the
        // antiforgery token requirement (which targets the cookie/form surface) must be
        // turned off here, matching ApiAuthEndpoints / ApiTicketsEndpoints.
        group
            .MapPost("/check", HandleCheckAsync)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
                Policy = AuthorizationPolicies.IsAuthenticated,
            })
            .DisableAntiforgery()
            .WithName("ApiAuthzCheck");

        return endpoints;
    }

    /// <summary>
    /// Request body for <c>POST /api/v1/authz/check</c>. The subject is intentionally
    /// absent — it is derived server-side from the authenticated principal.
    /// </summary>
    /// <param name="Relation">
    /// Relation name as declared in <c>authz/model.fga</c> (e.g. <c>view</c>, <c>edit</c>).
    /// </param>
    /// <param name="Object">
    /// Fully-qualified object id including the type prefix (e.g. <c>ticket:42</c>).
    /// </param>
    public sealed record AuthzCheckRequest(string Relation, string Object);

    internal static async Task<IResult> HandleCheckAsync(
        HttpContext httpContext,
        [FromBody] AuthzCheckRequest? request,
        [FromServices] IOpenFgaAuthorizationService fga,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(fga);

        if (request is null)
        {
            return ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = new[] { "Request body is required." },
            });
        }

        Dictionary<string, string[]> errors = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(request.Relation))
        {
            errors[nameof(AuthzCheckRequest.Relation)] =
                new[] { "Relation is required." };
        }

        if (string.IsNullOrWhiteSpace(request.Object))
        {
            errors[nameof(AuthzCheckRequest.Object)] =
                new[] { "Object is required." };
        }

        if (errors.Count > 0)
        {
            return ValidationProblem(errors);
        }

        if (!TryGetActorId(httpContext, out Guid actorId))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        // Subject is always the caller — never client-supplied. Format as user:{id}
        // via the canonical TupleShapes helper so the tuple key matches every other
        // FGA call site in the app.
        string subject = TupleShapes.UserRef(actorId);

        bool allowed = await fga
            .CheckAsync(
                new FgaCheckRequest(
                    User: subject,
                    Relation: request.Relation,
                    Object: request.Object),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(new { allowed }, statusCode: StatusCodes.Status200OK);
    }

    private static bool TryGetActorId(HttpContext httpContext, out Guid actorId)
    {
        string? rawSub = httpContext.User.FindFirstValue("sub")
            ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(rawSub, out actorId);
    }

    private static IResult ValidationProblem(IDictionary<string, string[]> errors) =>
        Results.Json(
            new HttpValidationProblemDetails(errors)
            {
                Type = "https://datatracker.ietf.org/doc/html/rfc9110#name-400-bad-request",
                Title = "Bad Request",
                Status = StatusCodes.Status400BadRequest,
            },
            statusCode: StatusCodes.Status400BadRequest,
            contentType: ProblemJsonContentType);
}
