using CrewCall.Resources.Equipment;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Resources.Tests;

public sealed class EquipmentServiceTests(ResourcesDatabase database)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static string UniqueAssetCode() => $"ft-{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task Create_saves_valid_equipment_as_active_with_an_upper_case_asset_code()
    {
        await using var scope = database.CreateScope();
        var equipment = scope.ServiceProvider.GetRequiredService<EquipmentService>();
        var assetCode = UniqueAssetCode();

        var outcome = await equipment.CreateAsync(new CreateEquipment(" Fiber tester ", assetCode, null), Cancellation);

        var created = Assert.IsType<CreateEquipmentOutcome.Created>(outcome);
        Assert.Equal("Fiber tester", created.Equipment.Name);
        Assert.Equal(assetCode.ToUpperInvariant(), created.Equipment.AssetCode);
        Assert.True(created.Equipment.IsActive);
        Assert.Contains(await equipment.ListAsync(Cancellation), item => item.Id == created.Equipment.Id);
    }

    [Fact]
    public async Task Create_rejects_an_empty_name()
    {
        await using var scope = database.CreateScope();
        var equipment = scope.ServiceProvider.GetRequiredService<EquipmentService>();

        var outcome = await equipment.CreateAsync(new CreateEquipment("  ", UniqueAssetCode(), null), Cancellation);

        Assert.Contains("name", Assert.IsType<CreateEquipmentOutcome.Invalid>(outcome).Errors.Keys);
    }

    [Fact]
    public async Task Create_rejects_an_empty_asset_code()
    {
        await using var scope = database.CreateScope();
        var equipment = scope.ServiceProvider.GetRequiredService<EquipmentService>();

        var outcome = await equipment.CreateAsync(new CreateEquipment("Fiber tester", null, null), Cancellation);

        Assert.Contains("assetCode", Assert.IsType<CreateEquipmentOutcome.Invalid>(outcome).Errors.Keys);
    }

    [Fact]
    public async Task Create_rejects_a_duplicate_asset_code_regardless_of_case()
    {
        await using var scope = database.CreateScope();
        var equipment = scope.ServiceProvider.GetRequiredService<EquipmentService>();
        var assetCode = UniqueAssetCode();
        Assert.IsType<CreateEquipmentOutcome.Created>(await equipment.CreateAsync(new CreateEquipment("Fiber tester", assetCode, null), Cancellation));

        var outcome = await equipment.CreateAsync(new CreateEquipment("Another tester", assetCode.ToUpperInvariant(), null), Cancellation);

        Assert.Equal(assetCode.ToUpperInvariant(), Assert.IsType<CreateEquipmentOutcome.AssetCodeAlreadyExists>(outcome).AssetCode);
    }
}
