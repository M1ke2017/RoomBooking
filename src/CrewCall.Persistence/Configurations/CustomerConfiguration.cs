using CrewCall.WorkOrders.Customers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers", DatabaseSchemas.WorkOrders);

        builder.HasKey(customer => customer.Id);
        builder.Property(customer => customer.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(customer => customer.Name)
            .HasColumnName("name")
            .HasMaxLength(Customer.NameMaxLength)
            .IsRequired();

        builder.Property(customer => customer.ExternalReference)
            .HasColumnName("external_reference")
            .HasMaxLength(Customer.ExternalReferenceMaxLength);
    }
}
