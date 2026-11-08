using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using AdvisorCoachStudio.Web.Features.Sessions;
using AdvisorCoachStudio.Web.Infrastructure.AzureReports;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AdvisorCoachStudio.Web.Features.Coaching;

/// <summary>In-process report queue (not durable by design: the MVP keeps no storage).</summary>
public sealed class ReportQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(Guid sessionId) => _channel.Writer.TryWrite(sessionId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// Runs report generation off the request path. A per-session status guard prevents duplicate concurrent jobs,
/// and results are stored only if the session is still current (discarded sessions are never repopulated).
/// </summary>
public sealed class ReportWorker(
    ReportQueue queue,
    PracticeSessionStore store,
    CoachingReportGenerator generator,
    ProviderHealthTracker health,
    IOptionsMonitor<AzureReportsOptions> options,
    TimeProvider time,
    ILogger<ReportWorker> logger) : BackgroundService
{
    private static readonly Meter Meter = new("AdvisorCoachStudio.Reports");
    private static readonly Counter<long> Attempts = Meter.CreateCounter<long>("acs.reports.attempts");
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("acs.reports.outcomes");
    private static readonly Histogram<double> Latency = Meter.CreateHistogram<double>("acs.reports.latency_seconds");

    private readonly SemaphoreSlim _concurrency = new(2, 2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var sessionId in queue.ReadAllAsync(stoppingToken))
        {
            await _concurrency.WaitAsync(stoppingToken);
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessAsync(sessionId, stoppingToken);
                }
                finally
                {
                    _concurrency.Release();
                }
            }, CancellationToken.None);
        }
    }

    public async Task ProcessAsync(Guid sessionId, CancellationToken stoppingToken)
    {
        var session = store.Get(sessionId);
        if (session is null)
        {
            return;
        }

        var revision = session.TranscriptRevision;
        if (!session.TryMarkReportGenerating(revision))
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, session.Lifetime);
        var started = Stopwatch.GetTimestamp();
        var maxAttempts = Math.Clamp(options.CurrentValue.MaxAttempts, 1, 5);
        var turns = session.Turns();
        var hash = session.Snapshot().TranscriptHash;
        for (var attempt = 1; ; attempt++)
        {
            Attempts.Add(1);
            try
            {
                var report = await generator.GenerateAsync(session, turns, revision, hash, cts.Token);
                health.ReportsSucceeded();
                if (session.TryStoreReport(revision, report))
                {
                    Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "ready"));
                    Latency.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
                    logger.LogInformation("Report ready for session {SessionId} (assessment {Assessment}, attempts {Attempts}).", sessionId, report.AssessmentStatus, attempt);
                }
                else
                {
                    logger.LogInformation("Report for session {SessionId} was not stored because the session was discarded or reset.", sessionId);
                }

                return;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Session discarded or application stopping: store nothing.
                return;
            }
            catch (ReportProviderException ex) when (ex.IsTransient && attempt < maxAttempts)
            {
                var delay = ex.RetryAfter ?? TimeSpan.FromSeconds(Math.Pow(3, attempt));
                delay = TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, 30));
                logger.LogWarning("Report attempt {Attempt} for session {SessionId} failed ({Failure}); retrying in {Delay}s.", attempt, sessionId, ex.Failure, delay.TotalSeconds);
                try
                {
                    await Task.Delay(delay, time, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            catch (ReportProviderException ex)
            {
                health.ReportsFailed(ex.Failure == ReportFailure.Authentication);
                Fail(session, revision, ex.Failure switch
                {
                    ReportFailure.NotConfigured => "provider_not_configured",
                    ReportFailure.Authentication => "report_auth_failed",
                    ReportFailure.Throttled => "report_throttled",
                    ReportFailure.ContentFilter => "report_content_filtered",
                    ReportFailure.BadRequest => "report_rejected",
                    _ => "report_unavailable",
                });
                return;
            }
            catch (ReportGenerationException ex) when (ex.Retryable && ex.Code == "report_empty_output" && attempt < maxAttempts)
            {
                logger.LogWarning("Report attempt {Attempt} for session {SessionId} returned empty output; retrying.", attempt, sessionId);
            }
            catch (ReportGenerationException ex)
            {
                Fail(session, revision, ex.Code);
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected report failure for session {SessionId}.", sessionId);
                Fail(session, revision, "internal_error");
                return;
            }
        }
    }

    private void Fail(PracticeSession session, int revision, string code)
    {
        Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", code));
        if (session.TryMarkReportFailed(revision, code))
        {
            logger.LogWarning("Report generation failed for session {SessionId}: {Code}.", session.Id, code);
        }
    }
}
