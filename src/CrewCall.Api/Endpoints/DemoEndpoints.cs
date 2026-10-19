using CrewCall.Api.Demo;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CrewCall.Api.Endpoints;

/// <summary>Portfolio demo data, only where Demo:Seed is enabled (the AppHost enables it for local runs).</summary>
internal static class DemoEndpoints
{
    public const string SeedSetting = "Demo:Seed";

    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        if (app.ServiceProvider.GetRequiredService<IConfiguration>().GetValue<bool>(SeedSetting))
        {
            app.MapPost("/api/demo/seed", SeedAsync).WithTags("Demo").WithName("SeedDemoData");
        }

        return app;
    }

    /// <summary>Creates today's demo plan if it does not exist yet (idempotent); 200 either way.</summary>
    private static async Task<Ok<DemoSeedResult>> SeedAsync(DemoSeeder seeder, CancellationToken cancellationToken) =>
        TypedResults.Ok(await seeder.SeedAsync(cancellationToken));
}
