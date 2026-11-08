using AdvisorCoachStudio.Web.Contracts;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Access;
using AdvisorCoachStudio.Web.Features.Coaching;
using AdvisorCoachStudio.Web.Features.Scenarios;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using AdvisorCoachStudio.Web.Infrastructure.Http;
using AdvisorCoachStudio.Web.Infrastructure.WebSockets;
using AdvisorCoachStudio.Web.Prompts;
using Microsoft.Extensions.Options;

namespace AdvisorCoachStudio.Web.Features.Sessions;

public static class SessionEndpoints
{
    public const string SessionCreatePolicy = "session-create";

    public static void MapApplicationApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter<ValidateAntiforgeryFilter>();

        api.MapGet("/capabilities", (ProviderReadiness readiness, IOptionsMonitor<RealtimeOptions> realtime) =>
        {
            var rt = readiness.Realtime();
            var rp = readiness.Reports();
            return Results.Ok(new CapabilitiesResponse(
                readiness.Mode,
                new CapabilityDto(rt.Status, rt.Code, rt.Message),
                new CapabilityDto(rp.Status, rp.Code, rp.Message),
                readiness.DemonstrationAvailable,
                readiness.IsSimulation,
                readiness.IsSimulation ? ProviderReadiness.SimulationLabel : null,
                Math.Max(1, realtime.CurrentValue.MaxSessionMinutes),
                DataNotice.Text));
        });

        api.MapGet("/scenarios", (ScenarioCatalog catalog) => Results.Ok(catalog.PublicCatalog()));

        api.MapGet("/scenarios/{scenarioId}", (string scenarioId, ScenarioCatalog catalog, HttpContext http) =>
            catalog.TryGet(scenarioId, out var scenario) ? Results.Ok(ScenarioCatalog.ToPublic(scenario)) : ApiProblems.Create(http, 404, "scenario_not_found", "That scenario was not found."));

        var sessions = api.MapGroup("/sessions").WithNoStore();

        sessions.MapPost("/", (HttpContext http, CreateSessionRequest request, ScenarioCatalog catalog, ProviderReadiness readiness,
            PracticeSessionStore store, LiveConnectionLimiter limiter, AccessCodeService access, IOptionsMonitor<RealtimeOptions> realtime) =>
        {
            if (!access.TryGetOwner(http.User, out var owner))
            {
                return ApiProblems.Unauthorized(http);
            }

            if (!catalog.TryGet(request.ScenarioId, out var scenario))
            {
                return ApiProblems.Create(http, 400, "invalid_scenario", "Choose one of the available scenarios.");
            }

            if (!DifficultyInfo.TryParse(request.Difficulty, out var difficulty))
            {
                return ApiProblems.Create(http, 400, "invalid_difficulty", "Choose Beginner, Intermediate, or Advanced.");
            }

            if (!request.AcknowledgedNotice)
            {
                return ApiProblems.Create(http, 400, "notice_not_acknowledged", "Please read and acknowledge the privacy notice before starting.");
            }

            var capability = readiness.Realtime();
            if (!capability.IsReady)
            {
                return capability.Status == ProviderReadinessStatus.SetupRequired
                    ? ApiProblems.ProviderNotConfigured(http, capability.Message)
                    : ApiProblems.Create(http, 503, capability.Code, capability.Message, retryable: true, retryAfter: TimeSpan.FromSeconds(30));
            }

            // The owner's previous session (if any) is discarded on creation, so only global capacity matters here.
            var current = store.GetCurrent(owner.OwnerId);
            if (limiter.Check(owner.OwnerId) == LiveSlotResult.AtCapacity && (current is null || !SessionStateMachine.IsLive(current.State)))
            {
                return ApiProblems.Create(http, 429, "capacity_reached", "All practice slots are busy. Please try again in a minute.", retryable: true, retryAfter: TimeSpan.FromSeconds(60));
            }

            var session = store.Create(owner, scenario, difficulty, RoleplayPromptBuilder.Version, readiness.IsSimulation);
            var minutes = Math.Max(1, realtime.CurrentValue.MaxSessionMinutes);
            return Results.Created($"/api/sessions/{session.Id}", new CreateSessionResponse(
                session.Id, $"/practice/{session.Id}", $"/ws/sessions/{session.Id}", $"/sessions/{session.Id}/report", minutes * 60, session.IsSimulated));
        }).RequireRateLimiting(SessionCreatePolicy);

        sessions.MapGet("/{sessionId:guid}", (Guid sessionId, HttpContext http, PracticeSessionStore store, AccessCodeService access) =>
            Owned(http, sessionId, store, access) is { } session ? Results.Ok(SessionStatusResponse.From(session.Snapshot())) : ApiProblems.NotFound(http));

        sessions.MapPost("/{sessionId:guid}/complete", (Guid sessionId, HttpContext http, PracticeSessionStore store, AccessCodeService access, TimeProvider time) =>
        {
            if (Owned(http, sessionId, store, access) is not { } session)
            {
                return ApiProblems.NotFound(http);
            }

            if (session.State == ConversationState.Created)
            {
                session.TryTransition(ConversationState.Expired, time.GetUtcNow().UtcDateTime);
            }
            else if (SessionStateMachine.IsLive(session.State))
            {
                session.RequestEnd();
                return Results.Accepted($"/api/sessions/{sessionId}", SessionStatusResponse.From(session.Snapshot()));
            }

            return Results.Ok(SessionStatusResponse.From(session.Snapshot()));
        });

        sessions.MapGet("/{sessionId:guid}/transcript", (Guid sessionId, HttpContext http, PracticeSessionStore store, AccessCodeService access) =>
            Owned(http, sessionId, store, access) is { } session
                ? Results.Ok(TranscriptResponse.From(session.Snapshot(), session.Turns()))
                : ApiProblems.NotFound(http));

        sessions.MapGet("/{sessionId:guid}/report", (Guid sessionId, HttpContext http, PracticeSessionStore store, AccessCodeService access) =>
            Owned(http, sessionId, store, access) is { } session
                ? Results.Ok(ReportResponse.From(session.Snapshot(), session.Report))
                : ApiProblems.NotFound(http));

        sessions.MapPost("/{sessionId:guid}/report/retry", (Guid sessionId, HttpContext http, PracticeSessionStore store, AccessCodeService access, ProviderReadiness readiness, ReportQueue queue) =>
        {
            if (Owned(http, sessionId, store, access) is not { } session)
            {
                return ApiProblems.NotFound(http);
            }

            var snapshot = session.Snapshot();
            if (snapshot.TranscriptRevision == 0)
            {
                return ApiProblems.Conflict(http, "transcript_not_final", "The report can be requested after the conversation ends.");
            }

            switch (snapshot.ReportStatus)
            {
                case ReportStatus.Ready:
                    return ApiProblems.Conflict(http, "report_ready", "The report is already available.");
                case ReportStatus.Queued or ReportStatus.Generating:
                    return Results.Accepted($"/api/sessions/{sessionId}/report", ReportResponse.From(snapshot, null));
                case ReportStatus.Failed when !PracticeSession.IsRetryableReportError(snapshot.ReportErrorCode):
                    return ApiProblems.Conflict(http, "report_not_retryable", "A coaching report cannot be generated for this transcript. Start a new attempt instead.");
            }

            if (readiness.Reports().Status == ProviderReadinessStatus.SetupRequired)
            {
                return ApiProblems.ProviderNotConfigured(http, readiness.Reports().Message);
            }

            if (session.TryQueueReport(userRetry: true))
            {
                queue.Enqueue(session.Id);
            }
            else if (session.ReportRequestCount >= PracticeSession.MaxReportRequests)
            {
                return ApiProblems.Create(http, 429, "retry_limit_reached", "The report has been retried too many times. Start a new attempt instead.");
            }

            return Results.Accepted($"/api/sessions/{sessionId}/report", ReportResponse.From(session.Snapshot(), null));
        });

        sessions.MapDelete("/{sessionId:guid}", (Guid sessionId, HttpContext http, PracticeSessionStore store, AccessCodeService access) =>
        {
            if (!access.TryGetOwner(http.User, out var owner) || !store.Discard(sessionId, owner.OwnerId))
            {
                return ApiProblems.NotFound(http);
            }

            return Results.NoContent();
        });

        sessions.MapPost("/{sessionId:guid}/demonstrations", (Guid sessionId, HttpContext http, PracticeSessionStore store, AccessCodeService access,
            ProviderReadiness readiness, LiveConnectionLimiter limiter, TimeProvider time, IOptionsMonitor<RealtimeOptions> realtime) =>
        {
            if (Owned(http, sessionId, store, access) is not { } session)
            {
                return ApiProblems.NotFound(http);
            }

            if (!readiness.DemonstrationAvailable)
            {
                return ApiProblems.ProviderNotConfigured(http, "Demonstrations are not available right now.");
            }

            if (session.State != ConversationState.Ended)
            {
                return ApiProblems.Conflict(http, "session_not_ended", "Demonstrations are available after your practice conversation ends.");
            }

            if (limiter.Check(session.OwnerId) != LiveSlotResult.Acquired)
            {
                return ApiProblems.Create(http, 429, "capacity_reached", "Please wait for the current audio to finish, then try again.", retryable: true, retryAfter: TimeSpan.FromSeconds(30));
            }

            var demonstration = session.TryCreateDemonstration(DemonstrationPromptBuilder.Version, readiness.IsSimulation, time.GetUtcNow().UtcDateTime);
            return demonstration is null
                ? ApiProblems.Conflict(http, "demonstration_in_progress", "A demonstration is already playing.")
                : Results.Created($"/api/sessions/{sessionId}", new CreateDemonstrationResponse(demonstration.Id, $"/ws/demonstrations/{demonstration.Id}", realtime.CurrentValue.DemonstrationMaxSeconds, demonstration.IsSimulated));
        });
    }

    private static PracticeSession? Owned(HttpContext http, Guid sessionId, PracticeSessionStore store, AccessCodeService access) =>
        access.TryGetOwner(http.User, out var owner) ? store.GetOwned(sessionId, owner.OwnerId) : null;
}

public static class DataNotice
{
    public const string Text =
        "This is a fictional training simulation. Do not enter real client names, account details, personal information, or confidential financial information. " +
        "Your audio is sent to Azure AI for the conversation and transcription. This application does not store raw audio. " +
        "It keeps the transcript and coaching report in server memory only for your current session; they are discarded when you start a new session, sign out, or the service restarts. " +
        "AI feedback is for communication practice only, not financial, legal, investment, or compliance advice.";
}
