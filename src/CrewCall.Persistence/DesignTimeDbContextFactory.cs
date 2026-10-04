using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CrewCall.Persistence;

/// <summary>
/// Used only by the EF Core tools (dotnet ef migrations ...). Adding a migration does not open a connection,
/// so the placeholder below is never used to connect. Set CREWCALL_DESIGN_TIME_CONNECTION to run tools
/// that need a real database.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CrewCallDbContext>
{
    private const string PlaceholderConnectionString = "Host=design-time-placeholder;Database=crewcall";

    public CrewCallDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("CREWCALL_DESIGN_TIME_CONNECTION")
            ?? PlaceholderConnectionString;

        var builder = new DbContextOptionsBuilder<CrewCallDbContext>();
        PersistenceServiceCollectionExtensions.ConfigureNpgsql(builder, connectionString);

        return new CrewCallDbContext(builder.Options);
    }
}
