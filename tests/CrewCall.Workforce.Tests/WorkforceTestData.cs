using CrewCall.Workforce.Technicians;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Workforce.Tests;

/// <summary>Creates prerequisite records with unique values, so tests sharing the database do not interfere.</summary>
internal static class WorkforceTestData
{
    private static int _dateSequence;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public static async Task<Guid> CreateTechnicianAsync(
        WorkforceDatabase database, string timeZoneId = "Europe/Warsaw", string countryCode = "PL", bool isActive = true)
    {
        await using var scope = database.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<TechnicianService>().CreateAsync(
            new CreateTechnician("Technician", $"tech-{Guid.NewGuid():N}@crewcall.test", isActive, timeZoneId, countryCode),
            Cancellation);
        return Assert.IsType<CreateTechnicianOutcome.Created>(outcome).Technician.Id;
    }

    /// <summary>
    /// A date no other test uses. Holidays are shared by every technician of a country, so tests that add holidays use
    /// far-future dates that never collide with each other or with the fixed dates of the other availability tests.
    /// </summary>
    public static DateOnly UniqueHolidayDate() => new DateOnly(2200, 1, 1).AddDays(Interlocked.Increment(ref _dateSequence));
}
