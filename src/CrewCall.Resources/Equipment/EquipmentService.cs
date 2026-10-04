using Microsoft.EntityFrameworkCore;

namespace CrewCall.Resources.Equipment;

public sealed class EquipmentService(IResourcesDbContext db)
{
    public async Task<CreateEquipmentOutcome> CreateAsync(CreateEquipment command, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var name = errors.Required("name", command.Name, EquipmentItem.NameMaxLength);
        var assetCode = errors.Required("assetCode", command.AssetCode, EquipmentItem.AssetCodeMaxLength)?.ToUpperInvariant();

        if (errors.Any)
        {
            return new CreateEquipmentOutcome.Invalid(errors.ToDictionary());
        }

        if (await AssetCodeExistsAsync(assetCode!, cancellationToken))
        {
            return new CreateEquipmentOutcome.AssetCodeAlreadyExists(assetCode!);
        }

        var equipment = new EquipmentItem(Guid.CreateVersion7(), name!, assetCode!, command.IsActive ?? true);
        db.Equipment.Add(equipment);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent request may have taken the same asset code; the unique index rejected this one.
            if (await AssetCodeExistsAsync(assetCode!, cancellationToken))
            {
                return new CreateEquipmentOutcome.AssetCodeAlreadyExists(assetCode!);
            }

            throw;
        }

        return new CreateEquipmentOutcome.Created(equipment);
    }

    public async Task<IReadOnlyList<EquipmentItem>> ListAsync(CancellationToken cancellationToken) =>
        await db.Equipment
            .AsNoTracking()
            .OrderBy(equipment => equipment.Name)
            .ThenBy(equipment => equipment.AssetCode)
            .ToListAsync(cancellationToken);

    private Task<bool> AssetCodeExistsAsync(string assetCode, CancellationToken cancellationToken) =>
        db.Equipment.AnyAsync(equipment => equipment.AssetCode == assetCode, cancellationToken);
}
