using Microsoft.Extensions.Options;

namespace Lateral.Cms.Ingestion;

/// <summary>
/// Runs <see cref="BatchProcessor"/> in the background: drains the queue, then sleeps until a batch is accepted
/// (or the poll interval passes, which picks up retries that have come due).
/// </summary>
/// <remarks>
/// It never stops on an error: an unhandled exception in a background service would take the whole host down, so
/// every error is logged and the loop carries on.
/// </remarks>
public sealed class BatchWorker(
    IServiceScopeFactory scopes,
    WakeSignal signal,
    TimeProvider time,
    IOptions<InboxOptions> options,
    ILogger<BatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting; nothing here should hold it up.
        await Task.Yield();

        var settings = options.Value;
        if (!settings.WorkerEnabled)
        {
            IngestionLog.WorkerDisabled(logger);
            return;
        }

        IngestionLog.WorkerStarted(logger);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<BatchProcessor>();

                var step = await processor.ProcessNextAsync(stoppingToken);
                await processor.RefreshBacklogAsync(stoppingToken);

                if (step == ProcessStep.Idle)
                {
                    await signal.WaitAsync(settings.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                IngestionLog.WorkerFault(logger, ex);
                try
                {
                    await Task.Delay(settings.FaultDelay, time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        IngestionLog.WorkerStopped(logger);
    }
}
