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

        var outcome = await technicians.CreateAsync(new CreateTechnician(" Anna Kowalska ", email.ToUpperInvariant(), null), Cancellation);

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
                .CreateAsync(new CreateTechnician("Jan Nowak", email, IsActive: false), Cancellation);
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

        var outcome = await technicians.CreateAsync(new CreateTechnician(displayName, UniqueEmail(), null), Cancellation);

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

        var outcome = await technicians.CreateAsync(new CreateTechnician("Anna Kowalska", email, null), Cancellation);

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
            await technicians.CreateAsync(new CreateTechnician("First", email, null), Cancellation));

        var outcome = await technicians.CreateAsync(new CreateTechnician("Second", email.ToUpperInvariant(), null), Cancellation);

        var duplicate = Assert.IsType<CreateTechnicianOutcome.EmailAlreadyExists>(outcome);
        Assert.Equal(email, duplicate.Email);
    }
}
