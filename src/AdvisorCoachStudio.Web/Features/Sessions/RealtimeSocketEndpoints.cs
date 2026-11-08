using System.Net.WebSockets;
using AdvisorCoachStudio.Web.Contracts;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Access;
using AdvisorCoachStudio.Web.Features.Coaching;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using AdvisorCoachStudio.Web.Infrastructure.Http;
using AdvisorCoachStudio.Web.Infrastructure.WebSockets;
using AdvisorCoachStudio.Web.Prompts;

namespace AdvisorCoachStudio.Web.Features.Sessions;

/// <summary>
/// WebSocket handshake validation (spec 7 step 9 and 15.2): cookie, exact Origin, subprotocol, ownership, state,
/// readiness, and capacity are all checked before the upgrade and before any Azure connection is opened.
/// </summary>
public static class RealtimeSocketEndpoints
{
    public static void MapRealtimeSockets(this IEndpointRouteBuilder app)
    {
        app.Map("/ws/sessions/{sessionId:guid}", async (HttpContext http, Guid sessionId, PracticeSessionStore store, AccessCodeService access,
            OriginValidator origins, ProviderReadiness readiness, LiveConnectionLimiter limiter, RealtimeRelayFactory relays) =>
        {
            if (await ValidateHandshakeAsync(http, access, origins) is not { } owner)
            {
                return;
            }

            var session = store.GetOwned(sessionId, owner.OwnerId);
            if (session is null)
            {
                await WriteAsync(http, ApiProblems.NotFound(http));
                return;
            }

            if (session.State != ConversationState.Created)
            {
                await WriteAsync(http, ApiProblems.Conflict(http, "session_not_startable", "This practice session has already started or ended. Start a new session."));
                return;
            }

            if (!readiness.Realtime().IsReady)
            {
                await WriteAsync(http, ApiProblems.ProviderNotConfigured(http, readiness.Realtime().Message));
                return;
            }

            var (result, lease) = await limiter.AcquireAsync(owner.OwnerId, TimeSpan.FromSeconds(3), http.RequestAborted);
            if (lease is null)
            {
                await WriteAsync(http, result == LiveSlotResult.OwnerBusy
                    ? ApiProblems.Conflict(http, "connection_active", "Another practice connection is already active for this browser.")
                    : ApiProblems.Create(http, 429, "capacity_reached", "All practice slots are busy. Please try again in a minute.", retryable: true, retryAfter: TimeSpan.FromSeconds(60)));
                return;
            }

            using (lease)
            {
                using var socket = await AcceptAsync(http);
                var relay = relays.CreateRoleplay(session, socket, http.User, owner, ApiProblems.CorrelationId(http));
                await relay.RunAsync(http.RequestAborted);
            }
        });

        app.Map("/ws/demonstrations/{demonstrationId:guid}", async (HttpContext http, Guid demonstrationId, PracticeSessionStore store, AccessCodeService access,
            OriginValidator origins, ProviderReadiness readiness, LiveConnectionLimiter limiter, RealtimeRelayFactory relays) =>
        {
            if (await ValidateHandshakeAsync(http, access, origins) is not { } owner)
            {
                return;
            }

            if (store.FindDemonstration(demonstrationId, owner.OwnerId) is not var (session, demonstration))
            {
                await WriteAsync(http, ApiProblems.NotFound(http));
                return;
            }

            if (demonstration.Snapshot().Status != DemonstrationStatus.Created)
            {
                await WriteAsync(http, ApiProblems.Conflict(http, "demonstration_not_startable", "This demonstration has already been played. Request a new one."));
                return;
            }

            if (!readiness.DemonstrationAvailable)
            {
                await WriteAsync(http, ApiProblems.ProviderNotConfigured(http, "Demonstrations are not available right now."));
                return;
            }

            var (result, lease) = await limiter.AcquireAsync(owner.OwnerId, TimeSpan.FromSeconds(3), http.RequestAborted);
            if (lease is null)
            {
                await WriteAsync(http, ApiProblems.Create(http, result == LiveSlotResult.OwnerBusy ? 409 : 429, "capacity_reached", "Please try again in a minute.", retryable: true));
                return;
            }

            using (lease)
            {
                // Only server-owned rubric text reaches the demonstration prompt (never model-generated report prose).
                var focus = (session.Report?.Content.Improvements ?? [])
                    .Select(improvement => improvement.CategoryId)
                    .Distinct(StringComparer.Ordinal)
                    .Select(id => Rubric.Categories.FirstOrDefault(category => category.Id == id))
                    .OfType<RubricCategory>()
                    .Take(2)
                    .Select(category => $"{category.DisplayName}: {category.Assessment}")
                    .ToList();
                using var socket = await AcceptAsync(http);
                var relay = relays.CreateDemonstration(session, demonstration, socket, http.User, owner, ApiProblems.CorrelationId(http), DemonstrationPromptBuilder.Build(session.Scenario, focus));
                await relay.RunAsync(http.RequestAborted);
            }
        });
    }

    private static async Task<AccessOwner?> ValidateHandshakeAsync(HttpContext http, AccessCodeService access, OriginValidator origins)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            await WriteAsync(http, ApiProblems.Create(http, 400, "websocket_required", "This endpoint only accepts WebSocket connections."));
            return null;
        }

        if (!access.TryGetOwner(http.User, out var owner))
        {
            await WriteAsync(http, ApiProblems.Unauthorized(http));
            return null;
        }

        if (!origins.IsAllowed(http))
        {
            await WriteAsync(http, ApiProblems.Create(http, 403, "origin_not_allowed", "This connection origin is not allowed."));
            return null;
        }

        if (!http.WebSockets.WebSocketRequestedProtocols.Contains(AppProtocol.Subprotocol))
        {
            await WriteAsync(http, ApiProblems.Create(http, 400, "unsupported_subprotocol", $"The '{AppProtocol.Subprotocol}' subprotocol is required."));
            return null;
        }

        return owner;
    }

    private static Task<WebSocket> AcceptAsync(HttpContext http) =>
        http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = AppProtocol.Subprotocol,
            KeepAliveInterval = TimeSpan.FromSeconds(15),
            DangerousEnableCompression = false,
        });

    private static Task WriteAsync(HttpContext http, IResult result) => result.ExecuteAsync(http);
}
