using CrewCall.Workforce.Holidays;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class HolidayCalendarEntryConfiguration : IEntityTypeConfiguration<HolidayCalendarEntry>
{
    public void Configure(EntityTypeBuilder<HolidayCalendarEntry> builder)
    {
        builder.ToTable("holiday_calendar", DatabaseSchemas.Workforce, table =>
        {
            table.HasCheckConstraint("ck_holiday_calendar_country_code", "country_code ~ '^[A-Z]{2}$'");
            table.HasCheckConstraint("ck_holiday_calendar_region_code", "region_code IS NULL OR region_code ~ '^[A-Z0-9]{1,3}$'");
        });

        builder.HasKey(holiday => holiday.Id);
        builder.Property(holiday => holiday.Id).HasColumnName("id").ValueGeneratedNever();

        // DateOnly -> PostgreSQL "date": a calendar day, interpreted in each technician's own time zone.
        builder.Property(holiday => holiday.Date).HasColumnName("date").IsRequired();
        builder.Property(holiday => holiday.Name).HasColumnName("name").HasMaxLength(HolidayCalendarEntry.NameMaxLength).IsRequired();
        builder.Property(holiday => holiday.CountryCode).HasColumnName("country_code").HasMaxLength(2).IsFixedLength().IsRequired();
        builder.Property(holiday => holiday.RegionCode).HasColumnName("region_code").HasMaxLength(HolidayCalendarEntry.RegionCodeMaxLength);

        // One entry per country, region and day. NULLS NOT DISTINCT: "no region" (national) counts as a value, so a
        // country cannot have two national entries on one day. Leading (country_code, date) serves lookups by country.
        builder.HasIndex(holiday => new { holiday.CountryCode, holiday.Date, holiday.RegionCode })
            .IsUnique()
            .AreNullsDistinct(false);
    }
}
