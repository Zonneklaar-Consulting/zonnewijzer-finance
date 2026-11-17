using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Backend.Data;
using Backend.Services.Daemon;
using Backend.Services.IntegrationDaemon;
using Backend.Services.Sigenergy;
using Microsoft.Extensions.Options;

namespace Backend.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin,Owner")]
public class AdminIntegrationController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IIntegrationDaemonClient _integrationDaemonClient;
    private readonly SigenergyDiagnosticsService _diagnostics;
    private readonly SigenergyOptions _sigenergyOptions;

    public AdminIntegrationController(
        ApplicationDbContext db,
        IIntegrationDaemonClient integrationDaemonClient,
        SigenergyDiagnosticsService diagnostics,
        IOptions<SigenergyOptions> sigenergyOptions)
    {
        _db = db;
        _integrationDaemonClient = integrationDaemonClient;
        _diagnostics = diagnostics;
        _sigenergyOptions = sigenergyOptions.Value;
    }

    [HttpPost("smappee/refresh/service-location-info")]
    public IActionResult RefreshSmappeeServiceLocationInfo() =>
        StatusCode(410, new { error = "This Smappee refresh is no longer available." });

    [HttpPost("smappee/refresh/service-locations-list")]
    public IActionResult RefreshSmappeeServiceLocationsList() =>
        StatusCode(410, new { error = "This Smappee refresh is no longer available." });

    [HttpPost("smappee/refresh/rfids")]
    public IActionResult RefreshSmappeeRfids() =>
        StatusCode(410, new { error = "This Smappee refresh is no longer available." });

    [HttpPost("sigenergy/refresh/energy-flow")]
    public async Task<IActionResult> RefreshSigenergy(CancellationToken cancellationToken)
    {
        var result = await _integrationDaemonClient.TriggerSigenergyUpdateAsync(cancellationToken);
        if (!result.Enabled)
            return StatusCode(503, new { ok = false, error = result.Error });
        if (!result.Success)
            return StatusCode(502, new { ok = false, error = result.Error, fetchedAtUtc = result.FetchedAtUtc });
        return Ok(new { ok = true, fetchedAtUtc = result.FetchedAtUtc, error = result.Error });
    }

    [HttpPost("sigenergy/diagnostics")]
    public async Task<IActionResult> RunSigenergyDiagnostics(CancellationToken cancellationToken)
    {
        var report = await _diagnostics.RunAsync(cancellationToken);
        return Ok(report);
    }

    [HttpGet("sigenergy/latest-state")]
    public async Task<IActionResult> GetLatestSigenergyState(CancellationToken cancellationToken)
    {
        var latest = await _db.SigenergyStatusSnapshots
            .AsNoTracking()
            .OrderByDescending(x => x.FetchedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest == null)
        {
            return Ok(new
            {
                hasSnapshot = false,
                usableBatteryCapacityKwh = _sigenergyOptions.UsableBatteryCapacityKwh,
                message = "No Sigenergy snapshot available yet. Wait for the next poll."
            });
        }

        return Ok(new
        {
            hasSnapshot = true,
            fetchedAtUtc = latest.FetchedAtUtc,
            isSuccess = latest.IsSuccess,
            configured = latest.Configured,
            error = latest.Error,
            batterySocPercent = latest.BatterySocPercent,
            batteryPowerKw = latest.BatteryPowerKw,
            solarPowerKw = latest.SolarPowerKw,
            remainingBatteryKwh = latest.RemainingBatteryKwh,
            usableBatteryCapacityKwh = _sigenergyOptions.UsableBatteryCapacityKwh
        });
    }
}
