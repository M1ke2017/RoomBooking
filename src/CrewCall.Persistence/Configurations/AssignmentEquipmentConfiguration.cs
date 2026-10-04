using CrewCall.Scheduling.Assignments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class AssignmentEquipmentConfiguration : IEntityTypeConfiguration<AssignmentEquipment>
{
    public void Configure(EntityTypeBuilder<AssignmentEquipment> builder)
    {
        builder.ToTable("assignment_equipment", DatabaseSchemas.Scheduling);

        // The composite key makes (assignment_id, equipment_id) unique: an asset appears once per assignment.
        builder.HasKey(equipment => new { equipment.AssignmentId, equipment.EquipmentId });
        builder.Property(equipment => equipment.AssignmentId).HasColumnName("assignment_id");

        // resources.equipment lives in another module's schema: no foreign key.
        builder.Property(equipment => equipment.EquipmentId).HasColumnName("equipment_id");
        builder.HasIndex(equipment => equipment.EquipmentId);
    }
}
