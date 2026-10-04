using System.Net;
using System.Net.Http.Json;
using CrewCall.Contracts.Equipment;
using CrewCall.Contracts.Vehicles;
using Xunit;

namespace CrewCall.Api.Tests;

public sealed class ResourceEndpointsTests(CrewCallApiFactory factory)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Post_vehicle_returns_201_and_get_returns_it()
    {
        using var client = factory.CreateClient();
        var registration = $"WV{Guid.NewGuid():N}"[..12];

        using var response = await client.PostAsJsonAsync("/api/vehicles", new CreateVehicleRequest(registration, "Van 1", "Van", null), Cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains(await client.GetFromJsonAsync<VehicleResponse[]>("/api/vehicles", Cancellation) ?? [],
            vehicle => vehicle.RegistrationNumber == registration.ToUpperInvariant());
    }

    [Fact]
    public async Task Post_vehicle_with_a_duplicate_registration_returns_409()
    {
        using var client = factory.CreateClient();
        var registration = $"wv{Guid.NewGuid():N}"[..12];
        (await client.PostAsJsonAsync("/api/vehicles", new CreateVehicleRequest(registration, "Van 1", null, null), Cancellation)).EnsureSuccessStatusCode();

        using var response = await client.PostAsJsonAsync("/api/vehicles", new CreateVehicleRequest(registration.ToUpperInvariant(), "Van 2", null, null), Cancellation);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Post_equipment_returns_201_and_get_returns_it()
    {
        using var client = factory.CreateClient();
        var assetCode = $"FT-{Guid.NewGuid():N}"[..20];

        using var response = await client.PostAsJsonAsync("/api/equipment", new CreateEquipmentRequest("Fiber tester", assetCode, null), Cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains(await client.GetFromJsonAsync<EquipmentResponse[]>("/api/equipment", Cancellation) ?? [],
            item => item.AssetCode == assetCode.ToUpperInvariant());
    }

    [Fact]
    public async Task Post_equipment_with_a_duplicate_asset_code_returns_409()
    {
        using var client = factory.CreateClient();
        var assetCode = $"ft-{Guid.NewGuid():N}"[..20];
        (await client.PostAsJsonAsync("/api/equipment", new CreateEquipmentRequest("Fiber tester", assetCode, null), Cancellation)).EnsureSuccessStatusCode();

        using var response = await client.PostAsJsonAsync("/api/equipment", new CreateEquipmentRequest("Another", assetCode.ToUpperInvariant(), null), Cancellation);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Post_equipment_with_an_empty_asset_code_returns_400()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/equipment", new CreateEquipmentRequest("Fiber tester", " ", null), Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
