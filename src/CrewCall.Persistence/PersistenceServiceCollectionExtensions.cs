using CrewCall.Resources;
using CrewCall.WorkOrders;
using CrewCall.Workforce;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CrewCall.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Name of the connection string supplied by configuration (by Aspire when run through the AppHost).</summary>
    public const string ConnectionStringName = "crewcall";

    public static IServiceCollection AddCrewCallPersistence(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<CrewCallDbContext>(options => ConfigureNpgsql(options, connectionString));

        // Each module resolves the same scoped context through its own narrow interface.
        services.AddScoped<IWorkOrdersDbContext>(provider => provider.GetRequiredService<CrewCallDbContext>());
        services.AddScoped<IWorkforceDbContext>(provider => provider.GetRequiredService<CrewCallDbContext>());
        services.AddScoped<IResourcesDbContext>(provider => provider.GetRequiredService<CrewCallDbContext>());

        return services;
    }

    internal static DbContextOptionsBuilder ConfigureNpgsql(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", DatabaseSchemas.Ops));
}
