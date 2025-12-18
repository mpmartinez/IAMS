using IAMS.Api.Data;
using IAMS.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Services;

/// <summary>
/// Background service that periodically checks warranty expiration dates
/// and creates alerts for assets with expiring or expired warranties.
/// </summary>
public class WarrantyCheckService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WarrantyCheckService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromHours(6); // Check every 6 hours

    public WarrantyCheckService(IServiceScopeFactory scopeFactory, ILogger<WarrantyCheckService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Warranty Check Service started");

        // Run initial check on startup
        await CheckWarrantiesAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_checkInterval, stoppingToken);
            await CheckWarrantiesAsync(stoppingToken);
        }
    }

    private async Task CheckWarrantiesAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Running warranty expiration check...");

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var today = DateTime.UtcNow.Date;
            var expiringThreshold = today.AddDays(90);

            // Get all assets with warranty end dates
            var assetsWithWarranty = await db.Assets
                .Where(a => a.WarrantyEndDate.HasValue)
                .Where(a => a.Status != AssetStatus.Retired && a.Status != AssetStatus.Lost)
                .Select(a => new
                {
                    a.Id,
                    a.WarrantyEndDate
                })
                .ToListAsync(cancellationToken);

            var alertsCreated = 0;
            var alertsUpdated = 0;

            foreach (var asset in assetsWithWarranty)
            {
                if (!asset.WarrantyEndDate.HasValue) continue;

                var warrantyEnd = asset.WarrantyEndDate.Value.Date;
                var daysRemaining = (warrantyEnd - today).Days;

                // Determine alert type
                string? alertType = null;
                if (daysRemaining < 0)
                {
                    alertType = WarrantyAlertTypes.Expired;
                }
                else if (daysRemaining <= 90)
                {
                    alertType = WarrantyAlertTypes.Expiring;
                }

                if (alertType == null) continue;

                // Check if alert already exists for this asset and type
                var existingAlert = await db.WarrantyAlerts
                    .FirstOrDefaultAsync(a => a.AssetId == asset.Id && a.AlertType == alertType, cancellationToken);

                if (existingAlert == null)
                {
                    // Check if we need to upgrade from Expiring to Expired
                    if (alertType == WarrantyAlertTypes.Expired)
                    {
                        var expiringAlert = await db.WarrantyAlerts
                            .FirstOrDefaultAsync(a => a.AssetId == asset.Id && a.AlertType == WarrantyAlertTypes.Expiring, cancellationToken);

                        if (expiringAlert != null)
                        {
                            // Remove the expiring alert when creating expired alert
                            db.WarrantyAlerts.Remove(expiringAlert);
                        }
                    }

                    // Create new alert
                    var alert = new WarrantyAlert
                    {
                        AssetId = asset.Id,
                        AlertType = alertType,
                        WarrantyEndDate = warrantyEnd,
                        DaysRemaining = daysRemaining
                    };

                    db.WarrantyAlerts.Add(alert);
                    alertsCreated++;
                }
                else
                {
                    // Update days remaining for existing unacknowledged alert
                    if (!existingAlert.IsAcknowledged && existingAlert.DaysRemaining != daysRemaining)
                    {
                        existingAlert.DaysRemaining = daysRemaining;
                        alertsUpdated++;
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Warranty check completed. Created {Created} alerts, updated {Updated} alerts",
                alertsCreated, alertsUpdated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during warranty expiration check");
        }
    }
}
