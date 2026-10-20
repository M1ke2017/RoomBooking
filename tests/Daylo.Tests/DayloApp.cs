using Daylo.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

[assembly: AssemblyFixture(typeof(Daylo.Tests.DayloApp))]

namespace Daylo.Tests;

/// <summary>
/// Daylo in-process against a real PostgreSQL (one container for the whole test run). The clock is fixed on
/// Tuesday 2041-03-12, 09:45 in Warsaw, so the demo day is always the same day.
/// </summary>
public sealed class DayloApp : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly DateTimeOffset Now = new(2041, 3, 12, 9, 45, 0, TimeSpan.FromHours(1));

    public static readonly DateOnly Day = WorkDay.Today(Now);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.3").Build();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development"); // migrations and the demo seed run on startup
        builder.UseSetting("ConnectionStrings:daylo", _postgres.GetConnectionString());
        builder.UseSetting("Demo:Seed", "true");
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new FixedClock(Now)));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
