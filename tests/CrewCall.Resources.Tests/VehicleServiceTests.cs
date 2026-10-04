using CrewCall.Resources.Vehicles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Resources.Tests;

public sealed class VehicleServiceTests(ResourcesDatabase database)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static string UniqueRegistration() => $"wv{Guid.NewGuid():N}"[..12];

    [Fact]
    public async Task Create_saves_a_valid_vehicle_as_active_with_an_upper_case_registration()
    {
        await using var scope = database.CreateScope();
        var vehicles = scope.ServiceProvider.GetRequiredService<VehicleService>();
        var registration = UniqueRegistration();

        var outcome = await vehicles.CreateAsync(new CreateVehicle($"  {registration}  ", " Van 1 ", "Van", null), Cancellation);

        var created = Assert.IsType<CreateVehicleOutcome.Created>(outcome);
        Assert.Equal(registration.ToUpperInvariant(), created.Vehicle.RegistrationNumber);
        Assert.Equal("Van 1", created.Vehicle.DisplayName);
        Assert.Equal("Van", created.Vehicle.VehicleType);
        Assert.True(created.Vehicle.IsActive);
        Assert.Contains(await vehicles.ListAsync(Cancellation), vehicle => vehicle.Id == created.Vehicle.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Create_rejects_an_empty_registration_number(string? registration)
    {
        await using var scope = database.CreateScope();
        var vehicles = scope.ServiceProvider.GetRequiredService<VehicleService>();

        var outcome = await vehicles.CreateAsync(new CreateVehicle(registration, "Van 1", null, null), Cancellation);

        Assert.Contains("registrationNumber", Assert.IsType<CreateVehicleOutcome.Invalid>(outcome).Errors.Keys);
    }

    [Fact]
    public async Task Create_rejects_a_duplicate_registration_number_regardless_of_case()
    {
        await using var scope = database.CreateScope();
        var vehicles = scope.ServiceProvider.GetRequiredService<VehicleService>();
        var registration = UniqueRegistration();
        Assert.IsType<CreateVehicleOutcome.Created>(await vehicles.CreateAsync(new CreateVehicle(registration, "Van 1", null, null), Cancellation));

        var outcome = await vehicles.CreateAsync(new CreateVehicle(registration.ToUpperInvariant(), "Van 2", null, null), Cancellation);

        Assert.Equal(registration.ToUpperInvariant(),
            Assert.IsType<CreateVehicleOutcome.RegistrationNumberAlreadyExists>(outcome).RegistrationNumber);
    }
}
