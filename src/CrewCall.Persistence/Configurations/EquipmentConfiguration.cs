using CrewCall.Resources.Equipment;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class EquipmentConfiguration : IEntityTypeConfiguration<EquipmentItem>
{
    public void Configure(EntityTypeBuilder<EquipmentItem> builder)
    {
        builder.ToTable("equipment", DatabaseSchemas.Resources);

        builder.HasKey(equipment => equipment.Id);
        builder.Property(equipment => equipment.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(equipment => equipment.Name).HasColumnName("name").HasMaxLength(EquipmentItem.NameMaxLength).IsRequired();

        // Upper-cased by the Resources module, so unique case-insensitively.
        builder.Property(equipment => equipment.AssetCode)
            .HasColumnName("asset_code")
            .HasMaxLength(EquipmentItem.AssetCodeMaxLength)
            .IsRequired();
        builder.HasIndex(equipment => equipment.AssetCode).IsUnique();

        builder.Property(equipment => equipment.IsActive).HasColumnName("is_active").IsRequired();
    }
}
