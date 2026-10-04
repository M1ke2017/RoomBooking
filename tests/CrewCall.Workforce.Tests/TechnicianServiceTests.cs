using CrewCall.Workforce.Technicians;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Workforce.Tests;

public sealed class TechnicianServiceTests(WorkforceDatabase database)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static string UniqueEmail() => $"tech-{Guid.NewGuid():N}@crewcall.test";

    [Fact]
    public async Task Create_saves_a_valid_technician_as_active_by_default_with_a_lower_case_email()
    {
        await using var scope = database.CreateScope();
        var technicians = scope.ServiceProvider.GetRequiredService<TechnicianService>();
        var email = UniqueEmail();

        var outcome = await technicians.CreateAsync(new CreateTechnician(" Anna Kowalska ", email.ToUpperInvariant(), null, "Europe/Warsaw", "PL"), Cancellation);

        var created = Assert.IsType<CreateTechnicianOutcome.Created>(outcome);
        Assert.Equal("Anna Kowalska", created.Technician.DisplayName);
        Assert.Equal(email, created.Technician.Email);
        Assert.True(created.Technician.IsActive);
    }

    [Fact]
    public async Task Create_persists_an_explicitly_inactive_technician_as_inactive()
    {
        var email = UniqueEmail();
        await using (var scope = database.CreateScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<TechnicianService>()
                .CreateAsync(new CreateTechnician("Jan Nowak", email, IsActive: false, "Europe/Warsaw", "PL"), Cancellation);
            Assert.IsType<CreateTechnicianOutcome.Created>(outcome);
        }

        // Read back through a new scope so the value comes from the database, not the change tracker.
        await using var readScope = database.CreateScope();
        var saved = (await readScope.ServiceProvider.GetRequiredService<TechnicianService>().ListAsync(Cancellation))
            .Single(technician => technician.Email == email);
        Assert.False(saved.IsActive);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Create_rejects_an_empty_display_name(string? displayName)
    {
        await using var scope = database.CreateScope();
        var technicians = scope.ServiceProvider.GetRequiredService<TechnicianService>();

        var outcome = await technicians.CreateAsync(new CreateTechnician(displayName, UniqueEmail(), null, "Europe/Warsaw", "PL"), Cancellation);

        var invalid = Assert.IsType<CreateTechnicianOutcome.Invalid>(outcome);
        Assert.Contains("displayName", invalid.Errors.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-an-email")]
    [InlineData("missing-domain@")]
    [InlineData("two@@crewcall.test")]
    [InlineData("no-dot@crewcall")]
    [InlineData("has space@crewcall.test")]
    public async Task Create_rejects_a_missing_or_invalid_email(string? email)
    {
        await using var scope = database.CreateScope();
        var technicians = scope.ServiceProvider.GetRequiredService<TechnicianService>();

        var outcome = await technicians.CreateAsync(new CreateTechnician("Anna Kowalska", email, null, "Europe/Warsaw", "PL"), Cancellation);

        var invalid = Assert.IsType<CreateTechnicianOutcome.Invalid>(outcome);
        Assert.Contains("email", invalid.Errors.Keys);
    }

    [Fact]
    public async Task Create_rejects_a_duplicate_email_regardless_of_case()
    {
        await using var scope = database.CreateScope();
        var technicians = scope.ServiceProvider.GetRequiredService<TechnicianService>();
        var email = UniqueEmail();
        Assert.IsType<CreateTechnicianOutcome.Created>(
            await technicians.CreateAsync(new CreateTechnician("First", email, null, "Europe/Warsaw", "PL"), Cancellation));

        var outcome = await technicians.CreateAsync(new CreateTechnician("Second", email.ToUpperInvariant(), null, "Europe/Warsaw", "PL"), Cancellation);

        var duplicate = Assert.IsType<CreateTechnicianOutcome.EmailAlreadyExists>(outcome);
        Assert.Equal(email, duplicate.Email);
    }

    [Fact]
    public async Task Create_persists_the_time_zone_and_an_upper_case_country_code()
    {
        var email = UniqueEmail();
        await using (var scope = database.CreateScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<TechnicianService>()
                .CreateAsync(new CreateTechnician("Maria Lopez", email, null, " America/New_York ", "us"), Cancellation);
            Assert.IsType<CreateTechnicianOutcome.Created>(outcome);
        }

        await using var readScope = database.CreateScope();
        var saved = (await readScope.ServiceProvider.GetRequiredService<TechnicianService>().ListAsync(Cancellation))
            .Single(technician => technician.Email == email);
        Assert.Equal("America/New_York", saved.TimeZoneId);
        Assert.Equal("US", saved.CountryCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("Central European Standard Time")]
    [InlineData("+02:00")]
    [InlineData("europe/warsaw")]
    public async Task Create_rejects_a_missing_or_non_IANA_time_zone(string? timeZoneId)
    {
        await using var scope = database.CreateScope();
        var technicians = scope.ServiceProvider.GetRequiredService<TechnicianService>();

        var outcome = await technicians.CreateAsync(new CreateTechnician("Anna Kowalska", UniqueEmail(), null, timeZoneId, "PL"), Cancellation);

        var invalid = Assert.IsType<CreateTechnicianOutcome.Invalid>(outcome);
        Assert.Equal(["timeZoneId"], invalid.Errors.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("P")]
    [InlineData("POL")]
    [InlineData("XX")]
    [InlineData("1A")]
    public async Task Create_rejects_a_missing_or_unknown_country_code(string? countryCode)
    {
        await using var scope = database.CreateScope();
        var technicians = scope.ServiceProvider.GetRequiredService<TechnicianService>();

        var outcome = await technicians.CreateAsync(new CreateTechnician("Anna Kowalska", UniqueEmail(), null, "Europe/Warsaw", countryCode), Cancellation);

        var invalid = Assert.IsType<CreateTechnicianOutcome.Invalid>(outcome);
        Assert.Equal(["countryCode"], invalid.Errors.Keys);
    }
}
