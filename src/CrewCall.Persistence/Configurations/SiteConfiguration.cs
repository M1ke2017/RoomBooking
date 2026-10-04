using CrewCall.WorkOrders.Customers;
using CrewCall.WorkOrders.Sites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class SiteConfiguration : IEntityTypeConfiguration<Site>
{
    public void Configure(EntityTypeBuilder<Site> builder)
    {
        builder.ToTable("sites", DatabaseSchemas.WorkOrders, table =>
        {
            table.HasCheckConstraint("ck_sites_latitude_range", "latitude IS NULL OR latitude BETWEEN -90 AND 90");
            table.HasCheckConstraint("ck_sites_longitude_range", "longitude IS NULL OR longitude BETWEEN -180 AND 180");
            table.HasCheckConstraint("ck_sites_coordinates_together", "(latitude IS NULL) = (longitude IS NULL)");
        });

        builder.HasKey(site => site.Id);
        builder.Property(site => site.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(site => site.CustomerId).HasColumnName("customer_id").IsRequired();

        // Customer 1 -> N Site. Restrict: a customer with sites cannot be deleted by accident.
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(site => site.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(site => site.Name).HasColumnName("name").HasMaxLength(Site.NameMaxLength).IsRequired();
        builder.Property(site => site.AddressLine1).HasColumnName("address_line1").HasMaxLength(Site.AddressLine1MaxLength);
        builder.Property(site => site.City).HasColumnName("city").HasMaxLength(Site.CityMaxLength).IsRequired();
        builder.Property(site => site.PostalCode).HasColumnName("postal_code").HasMaxLength(Site.PostalCodeMaxLength);

        builder.Property(site => site.CountryCode)
            .HasColumnName("country_code")
            .HasMaxLength(Site.CountryCodeLength)
            .IsFixedLength()
            .IsRequired();

        builder.Property(site => site.Latitude).HasColumnName("latitude");
        builder.Property(site => site.Longitude).HasColumnName("longitude");
    }
}
