using Backend.Services.Daemon;
using Backend.Services.Smappee;
using Microsoft.Extensions.Options;

namespace Backend.Services.IntegrationDaemon;

public sealed class IntegrationDaemonClient : IIntegrationDaemonClient
{
    private readonly ISmappeeChargingSyncService _sync;
    private readonly ISmappeePollingService _smappeePolling;
    private readonly ISigenergyPollingService _sigenergyPolling;
    private readonly IOptionsMonitor<IntegrationDaemonOptions> _options;
    private readonly ILogger<IntegrationDaemonClient> _logger;

    public IntegrationDaemonClient(
        ISmappeeChargingSyncService sync,
        ISmappeePollingService smappeePolling,
        ISigenergyPollingService sigenergyPolling,
        IOptionsMonitor<IntegrationDaemonOptions> options,
        ILogger<IntegrationDaemonClient> logger)
    {
        _sync = sync;
        _smappeePolling = smappeePolling;
        _sigenergyPolling = sigenergyPolling;
        _options = options;
        _logger = logger;
    }

    public async Task<SmappeeUpdateTriggerResult> TriggerSmappeeUpdateAsync(
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return new SmappeeUpdateTriggerResult(
                Success: false,
                Enabled: false,
                FetchedAtUtc: null,
                Error: "Smappee and Sigenergy updates are disabled.");
        }

        try
        {
            if (from.HasValue || to.HasValue)
            {
                if (!from.HasValue || !to.HasValue)
                {
                    return new SmappeeUpdateTriggerResult(
                        Success: false,
                        Enabled: true,
                        FetchedAtUtc: null,
                        Error: "Both 'from' and 'to' are required when specifying a range.");
                }

                if (to.Value < from.Value)
                {
                    return new SmappeeUpdateTriggerResult(
                        Success: false,
                        Enabled: true,
                        FetchedAtUtc: null,
                        Error: "'to' date must be on or after 'from' date.");
                }

                var syncResult = await _sync.SyncSmappeeCalendarAsync(
                    from.Value,
                    to.Value,
                    "Manual Smappee range sync",
                    cancellationToken);

                return new SmappeeUpdateTriggerResult(
                    Success: syncResult.Success,
                    Enabled: true,
                    FetchedAtUtc: DateTimeOffset.UtcNow,
                    Error: syncResult.ErrorMessage,
                    CardsAdded: syncResult.CardsAdded,
                    CardsUpdated: syncResult.CardsUpdated,
                    SessionsAdded: syncResult.SessionsAdded,
                    SessionsUpdated: syncResult.SessionsUpdated,
                    SessionsSkipped: syncResult.SessionsSkipped,
                    SessionsErrors: syncResult.SessionsErrors,
                    SessionsInferredClosed: syncResult.SessionsInferredClosed);
            }

            var snapshot = await _smappeePolling.PollSessionsAsync(cancellationToken);
            return new SmappeeUpdateTriggerResult(
                Success: snapshot.IsSuccess,
                Enabled: true,
                FetchedAtUtc: new DateTimeOffset(DateTime.SpecifyKind(snapshot.FetchedAtUtc, DateTimeKind.Utc)),
                Error: snapshot.Error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Smappee update failed");
            return new SmappeeUpdateTriggerResult(
                Success: false,
                Enabled: true,
                FetchedAtUtc: null,
                Error: "Smappee update failed.");
        }
    }

    public async Task<SigenergyUpdateTriggerResult> TriggerSigenergyUpdateAsync(CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return new SigenergyUpdateTriggerResult(
                Success: false,
                Enabled: false,
                FetchedAtUtc: null,
                Error: "Smappee and Sigenergy updates are disabled.");
        }

        try
        {
            var snapshot = await _sigenergyPolling.PollEnergyFlowAsync(cancellationToken);
            return new SigenergyUpdateTriggerResult(
                Success: snapshot.IsSuccess,
                Enabled: true,
                FetchedAtUtc: new DateTimeOffset(DateTime.SpecifyKind(snapshot.FetchedAtUtc, DateTimeKind.Utc)),
                Error: snapshot.Error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sigenergy update failed");
            return new SigenergyUpdateTriggerResult(
                Success: false,
                Enabled: true,
                FetchedAtUtc: null,
                Error: "Sigenergy update failed.");
        }
    }
}
