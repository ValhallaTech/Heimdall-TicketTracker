using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Core.Auditing;
using Heimdall.Core.Dtos;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models.Pagination;
using Heimdall.Web.Authorization.Policies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Heimdall.Web.Endpoints;

/// <summary>
/// Phase 5.6 (<c>docs/implementation/phase-5-checklist.md</c> steps 14-15) — the first
/// <c>/api/v1/*</c> ticket endpoints. Minimal API, JSON in / JSON out, bearer-only
/// (the cookie surface stays on the Razor pages). Per-instance authorisation flows
/// entirely through the existing Phase 3 named policies (<c>CanViewTicket</c>,
/// <c>CanEditTicket</c>, <c>CanAssignTicket</c>) so there is one and only one policy
/// stack in the application.
/// </summary>
/// <remarks>
/// <para>
/// The collection-shaped surfaces (list + create) are gated by <c>IsAuthenticated</c>,
/// matching the existing <c>Tickets.razor</c> / <c>NewTicket.razor</c> pages: the
/// per-ticket <c>CanViewTicket</c> / <c>CanEditTicket</c> policies are instance-level
/// (<see cref="OpenFgaAuthorizationHandler"/> resolves the object id from the
/// <c>ticketId</c> route value and deny-closes when it cannot — so applying them to a
/// collection endpoint would 403 every request). The list endpoint applies the
/// per-instance filter itself via
/// <see cref="IOpenFgaAuthorizationService.ListObjectsAsync(FgaListObjectsRequest, CancellationToken)"/>
/// (same <c>ListObjects("ticket", "view", user)</c> code path as <c>Tickets.razor</c>),
/// then pages through
/// <see cref="ITicketService.GetPagedByIdsAsync(IReadOnlyList{int}, PagedQuery, CancellationToken)"/>.
/// </para>
/// <para>
/// The assign endpoint composes <c>RequireMfa</c> with <c>CanAssignTicket</c> in the
/// same way the Razor admin surfaces do (Phase 4.6 step 18): both policies must
/// allow.
/// </para>
/// </remarks>
public static class ApiTicketsEndpoints
{
    private const string TicketIdRouteKey = AuthorizationPolicies.TicketIdRouteKey;
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;
    private const string ProblemJsonContentType = "application/problem+json";

    /// <summary>
    /// Maps the Phase 5.6 ticket endpoints onto <paramref name="endpoints"/>.
    /// Mapped from <c>Program.cs</c> immediately after <see cref="ApiAuthEndpoints.MapApiAuthEndpoints"/>.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns><paramref name="endpoints"/> for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is <c>null</c>.</exception>
    public static IEndpointRouteBuilder MapApiTicketsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints
            .MapGroup("/api/v1/tickets")
            .RequireRateLimiting("api-token")
            .WithTags("Tickets");

        group
            .MapGet("/", HandleListAsync)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
                Policy = AuthorizationPolicies.IsAuthenticated,
            })
            .WithName("ApiTicketsList");

        group
            .MapGet("/{" + TicketIdRouteKey + ":int}", HandleGetByIdAsync)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
                Policy = AuthorizationPolicies.CanViewTicket,
            })
            .WithName("ApiTicketsGetById");

        group
            .MapPost("/", HandleCreateAsync)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
                Policy = AuthorizationPolicies.IsAuthenticated,
            })
            .DisableAntiforgery()
            .WithName("ApiTicketsCreate");

        group
            .MapPut("/{" + TicketIdRouteKey + ":int}", HandleUpdateAsync)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
                Policy = AuthorizationPolicies.CanEditTicket,
            })
            .DisableAntiforgery()
            .WithName("ApiTicketsUpdate");

        // Assign requires both RequireMfa and CanAssignTicket — same composition
        // rule the Razor admin pages use (Phase 4.6 step 18). Both policies must
        // allow; either denial fails the request.
        group
            .MapPost("/{" + TicketIdRouteKey + ":int}/assign", HandleAssignAsync)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
                Policy = AuthorizationPolicies.RequireMfa,
            })
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
                Policy = AuthorizationPolicies.CanAssignTicket,
            })
            .DisableAntiforgery()
            .WithName("ApiTicketsAssign");

        return endpoints;
    }

    /// <summary>
    /// Request body for <c>POST /api/v1/tickets/{ticketId}/assign</c>.
    /// </summary>
    /// <param name="AssigneeId">
    /// The user id to assign the ticket to. Self-assign is supported via
    /// <see cref="ITicketService.ClaimTicketAsync"/> — pass the caller's own id.
    /// </param>
    public sealed record AssignTicketRequest(Guid AssigneeId);

    internal static async Task<IResult> HandleListAsync(
        HttpContext httpContext,
        [FromServices] IOpenFgaAuthorizationService fga,
        [FromServices] ITicketService ticketService,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] string? searchText,
        [FromQuery] string? sortField,
        [FromQuery] string? sortDirection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(fga);
        ArgumentNullException.ThrowIfNull(ticketService);

        if (!TryGetActorId(httpContext, out Guid actorId))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        IReadOnlyList<string> objectRefs;
        try
        {
            objectRefs = await fga
                .ListObjectsAsync(
                    new FgaListObjectsRequest(
                        TupleShapes.UserRef(actorId),
                        "view",
                        TupleShapes.TicketType),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            // Deny-closed per Phase 3.5 step 10 — the FGA adapter contract is
            // supposed to swallow transport failures and return empty, but if it
            // throws we treat it the same as "no allowed ids".
            ILoggerFactory loggerFactory = httpContext.RequestServices.GetRequiredService<ILoggerFactory>();
            ILogger logger = loggerFactory.CreateLogger("ApiTicketsEndpoints");
            logger.LogWarning(
                ex,
                "OpenFGA ListObjects threw for {ActorId}; returning empty page.",
                actorId);
            objectRefs = Array.Empty<string>();
        }

        List<int> allowedIds = ExtractTicketIds(objectRefs);

        SortDirection direction = ParseSortDirection(sortDirection);
        PagedQuery query = new(
            page: page ?? 1,
            pageSize: pageSize is null ? DefaultPageSize : Math.Clamp(pageSize.Value, 1, MaxPageSize),
            searchText: searchText,
            sortField: sortField ?? "DateCreated",
            sortDirection: direction);

        PagedResult<TicketDto> result = await ticketService
            .GetPagedByIdsAsync(allowedIds, query, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(result, statusCode: StatusCodes.Status200OK);
    }

    internal static async Task<IResult> HandleGetByIdAsync(
        HttpContext httpContext,
        [FromRoute(Name = TicketIdRouteKey)] int ticketId,
        [FromServices] ITicketService ticketService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(ticketService);

        TicketDto? dto = await ticketService
            .GetByIdAsync(ticketId, cancellationToken)
            .ConfigureAwait(false);

        if (dto is null)
        {
            // Policy gate (CanViewTicket) already cleared FGA for this ticket id,
            // so a missing row signals drift between FGA tuples and the DB (e.g.
            // a delete that hasn't yet reaped the tuple). Log a warning so the
            // SOC can correlate — the response stays 404 (changing to 410 Gone
            // is a product decision tracked out-of-scope).
            ILoggerFactory loggerFactory = httpContext.RequestServices.GetRequiredService<ILoggerFactory>();
            ILogger logger = loggerFactory.CreateLogger("ApiTicketsEndpoints");
            string? actorSub = httpContext.User.FindFirstValue("sub")
                ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            logger.LogWarning(
                "FGA/DB drift on GET /api/v1/tickets/{TicketId}: policy allowed view but ticket row is missing for actor sub={ActorSub}.",
                ticketId,
                actorSub ?? "(unknown)");
            return Results.NotFound();
        }

        return Results.Json(dto, statusCode: StatusCodes.Status200OK);
    }

    internal static async Task<IResult> HandleCreateAsync(
        HttpContext httpContext,
        [FromBody] TicketDto dto,
        [FromServices] ITicketService ticketService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(ticketService);

        if (dto is null)
        {
            return UnprocessableEntity("Request body is required.");
        }

        if (!TryValidate(dto, out IDictionary<string, string[]> errors))
        {
            return ValidationProblem(errors);
        }

        TicketDto created = await ticketService
            .CreateAsync(dto, cancellationToken)
            .ConfigureAwait(false);

        string location = string.Create(
            CultureInfo.InvariantCulture,
            $"/api/v1/tickets/{created.Id}");
        return Results.Created(location, created);
    }

    internal static async Task<IResult> HandleUpdateAsync(
        HttpContext httpContext,
        [FromRoute(Name = TicketIdRouteKey)] int ticketId,
        [FromBody] TicketDto dto,
        [FromServices] ITicketService ticketService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(ticketService);

        if (dto is null)
        {
            return UnprocessableEntity("Request body is required.");
        }

        if (dto.Id != ticketId)
        {
            dto.Id = ticketId;
        }

        if (!TryValidate(dto, out IDictionary<string, string[]> errors))
        {
            return ValidationProblem(errors);
        }

        bool updated = await ticketService
            .UpdateAsync(dto, cancellationToken)
            .ConfigureAwait(false);

        return updated ? Results.NoContent() : Results.NotFound();
    }

    internal static async Task<IResult> HandleAssignAsync(
        HttpContext httpContext,
        [FromRoute(Name = TicketIdRouteKey)] int ticketId,
        [FromBody] AssignTicketRequest? request,
        [FromServices] ITicketService ticketService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(ticketService);

        if (request is null || request.AssigneeId == Guid.Empty)
        {
            return ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(AssignTicketRequest.AssigneeId)] = new[] { "AssigneeId is required and must be a non-empty Guid." },
            });
        }

        if (!TryGetActorId(httpContext, out Guid actorId))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        // Pre-check existence so the 200 {applied:false} branch unambiguously
        // means "no-op (assignee already matches)" — the 404 branch means the
        // ticket row is missing. CanAssignTicket policy already cleared FGA for
        // this ticket id, so a missing row signals FGA/DB drift; warn so the
        // SOC can correlate. We accept a small TOCTOU window (the row could be
        // deleted between this check and AssignTicketAsync below — in that case
        // the service returns false and the caller sees 200 {applied:false}, the
        // same as a same-assignee no-op; that's the documented contract).
        TicketDto? existing = await ticketService
            .GetByIdAsync(ticketId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            ILoggerFactory loggerFactory = httpContext.RequestServices.GetRequiredService<ILoggerFactory>();
            ILogger logger = loggerFactory.CreateLogger("ApiTicketsEndpoints");
            string? actorSub = httpContext.User.FindFirstValue("sub")
                ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            logger.LogWarning(
                "FGA/DB drift on POST /api/v1/tickets/{TicketId}/assign: policy allowed assign but ticket row is missing for actor sub={ActorSub}.",
                ticketId,
                actorSub ?? "(unknown)");
            return Results.Json(
                new ProblemDetails
                {
                    Type = "https://datatracker.ietf.org/doc/html/rfc9110#name-404-not-found",
                    Title = "Not Found",
                    Status = StatusCodes.Status404NotFound,
                    Detail = "Ticket not found.",
                },
                statusCode: StatusCodes.Status404NotFound,
                contentType: ProblemJsonContentType);
        }

        try
        {
            bool applied = await ticketService
                .AssignTicketAsync(actorId, ticketId, request.AssigneeId, cancellationToken)
                .ConfigureAwait(false);

            // applied == false is the idempotent / no-op branch (assignee already
            // matches, or a concurrent delete raced the pre-check above). Return
            // 200 with the current state; the explicit 404 above covers the
            // policy-allowed / DB-missing drift case at request entry.
            return Results.Json(new { applied }, statusCode: StatusCodes.Status200OK);
        }
        catch (UnauthorizedAccessException)
        {
            // The policy gate is supposed to have caught this, but the service
            // re-checks defensively. Surface as 403, not 500.
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
    }

    private static List<int> ExtractTicketIds(IReadOnlyList<string> objectRefs)
    {
        string prefix = TupleShapes.TicketType + ":";
        List<int> ids = new(objectRefs.Count);
        foreach (ReadOnlyMemory<char> objectRef in objectRefs.Select(static r => r.AsMemory()))
        {
            if (objectRef.Span.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(
                    objectRef.Span[prefix.Length..],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static SortDirection ParseSortDirection(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return SortDirection.Descending;
        }

        if (Enum.TryParse(raw, ignoreCase: true, out SortDirection parsed))
        {
            return parsed;
        }

        if (string.Equals(raw, "asc", StringComparison.OrdinalIgnoreCase))
        {
            return SortDirection.Ascending;
        }

        if (string.Equals(raw, "desc", StringComparison.OrdinalIgnoreCase))
        {
            return SortDirection.Descending;
        }

        return SortDirection.Descending;
    }

    private static bool TryGetActorId(HttpContext httpContext, out Guid actorId)
    {
        string? rawSub = httpContext.User.FindFirstValue("sub")
            ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(rawSub, out actorId);
    }

    private static bool TryValidate(TicketDto dto, out IDictionary<string, string[]> errors)
    {
        Dictionary<string, List<string>> collected = new(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(dto.Title))
        {
            AddError(collected, nameof(TicketDto.Title), "Title is required.");
        }
        else if (dto.Title.Length > 200)
        {
            AddError(collected, nameof(TicketDto.Title), "Title must be 200 characters or fewer.");
        }

        if (string.IsNullOrWhiteSpace(dto.Description))
        {
            AddError(collected, nameof(TicketDto.Description), "Description is required.");
        }
        else if (dto.Description.Length > 4000)
        {
            AddError(collected, nameof(TicketDto.Description), "Description must be 4000 characters or fewer.");
        }

        if (dto.ProjectId == Guid.Empty)
        {
            AddError(collected, nameof(TicketDto.ProjectId), "ProjectId is required.");
        }

        if (dto.TeamId == Guid.Empty)
        {
            AddError(collected, nameof(TicketDto.TeamId), "TeamId is required.");
        }

        if (dto.ReporterId == Guid.Empty)
        {
            AddError(collected, nameof(TicketDto.ReporterId), "ReporterId is required.");
        }

        errors = collected.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
        return collected.Count == 0;
    }

    private static void AddError(Dictionary<string, List<string>> bag, string field, string message)
    {
        if (!bag.TryGetValue(field, out List<string>? list))
        {
            list = new List<string>(1);
            bag[field] = list;
        }

        list.Add(message);
    }

    private static IResult ValidationProblem(IDictionary<string, string[]> errors) =>
        Results.Json(
            new HttpValidationProblemDetails(errors)
            {
                Type = "https://datatracker.ietf.org/doc/html/rfc4918#section-11.2",
                Title = "Unprocessable Entity",
                Status = StatusCodes.Status422UnprocessableEntity,
            },
            statusCode: StatusCodes.Status422UnprocessableEntity,
            contentType: ProblemJsonContentType);

    private static IResult UnprocessableEntity(string detail) =>
        Results.Json(
            new ProblemDetails
            {
                Type = "https://datatracker.ietf.org/doc/html/rfc4918#section-11.2",
                Title = "Unprocessable Entity",
                Status = StatusCodes.Status422UnprocessableEntity,
                Detail = detail,
            },
            statusCode: StatusCodes.Status422UnprocessableEntity,
            contentType: ProblemJsonContentType);
}
