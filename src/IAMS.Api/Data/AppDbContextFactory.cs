using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace IAMS.Api.Data;

/// <summary>
/// Used only by `dotnet ef` at design time. Building the context directly keeps migration
/// commands from booting the web host (which refuses to start without a real connection
/// string) and from needing a reachable database - `migrations add` only reads the model.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // Same sources the app uses, so `database update` picks up user-secrets locally and
        // the environment variable in CI. Falls back to a placeholder that is never connected
        // to, which is all `migrations add` needs.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddUserSecrets<AppDbContextFactory>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? "Host=localhost;Database=iams_design_time;Username=postgres";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        // Deliberately the tenant-provider-less constructor: design time has no request
        // context, and the model should be generated without tenant filters applied.
        return new AppDbContext(options);
    }
}
