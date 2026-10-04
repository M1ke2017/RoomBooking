using CrewCall.Resources.Vehicles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class VehicleConfiguration : IEntityTypeConfiguration<Vehicle>
{
    public void Configure(EntityTypeBuilder<Vehicle> builder)
    {
        builder.ToTable("vehicles", DatabaseSchemas.Resources);

        builder.HasKey(vehicle => vehicle.Id);
        builder.Property(vehicle => vehicle.Id).HasColumnName("id").ValueGeneratedNever();

        // Upper-cased by the Resources module, so unique case-insensitively.
        builder.Property(vehicle => vehicle.RegistrationNumber)
            .HasColumnName("registration_number")
            .HasMaxLength(Vehicle.RegistrationNumberMaxLength)
            .IsRequired();
        builder.HasIndex(vehicle => vehicle.RegistrationNumber).IsUnique();

        builder.Property(vehicle => vehicle.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(Vehicle.DisplayNameMaxLength)
            .IsRequired();
        builder.Property(vehicle => vehicle.VehicleType).HasColumnName("vehicle_type").HasMaxLength(Vehicle.VehicleTypeMaxLength);
        builder.Property(vehicle => vehicle.IsActive).HasColumnName("is_active").IsRequired();
    }
}
