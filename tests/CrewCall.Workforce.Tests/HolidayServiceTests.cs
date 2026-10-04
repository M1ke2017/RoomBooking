using CrewCall.Workforce.Holidays;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Workforce.Tests;

public sealed class HolidayServiceTests(WorkforceDatabase database)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private async Task<CreateHolidayOutcome> CreateAsync(DateOnly? date, string? name, string? countryCode, string? regionCode = null)
    {
        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<HolidayService>()
            .CreateAsync(new CreateHoliday(date, name, countryCode, regionCode), Cancellation);
    }

    private async Task<IReadOnlyList<HolidayCalendarEntry>> ListAsync(string? countryCode)
    {
        await using var scope = database.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<HolidayService>().ListAsync(countryCode, Cancellation);
        return Assert.IsType<ListHolidaysOutcome.Listed>(outcome).Holidays;
    }

    [Fact]
    public async Task Create_saves_a_national_holiday_with_an_upper_case_country()
    {
        var date = WorkforceTestData.UniqueHolidayDate();

        var created = Assert.IsType<CreateHolidayOutcome.Created>(await CreateAsync(date, " Independence Day ", "pl"));

        Assert.Equal("Independence Day", created.Holiday.Name);
        Assert.Equal("PL", created.Holiday.CountryCode);
        Assert.Null(created.Holiday.RegionCode);
        Assert.Contains(await ListAsync("PL"), holiday => holiday.Id == created.Holiday.Id && holiday.Date == date);
    }

    [Fact]
    public async Task A_second_national_holiday_for_the_same_country_and_date_is_rejected()
    {
        var date = WorkforceTestData.UniqueHolidayDate();
        var first = Assert.IsType<CreateHolidayOutcome.Created>(await CreateAsync(date, "First", "PL"));

        var duplicate = Assert.IsType<CreateHolidayOutcome.AlreadyExists>(await CreateAsync(date, "Second", "pl"));

        Assert.Equal(first.Holiday.Id, duplicate.ExistingHolidayId);
    }

    [Fact]
    public async Task The_same_date_is_allowed_for_another_country_or_a_region()
    {
        var date = WorkforceTestData.UniqueHolidayDate();
        Assert.IsType<CreateHolidayOutcome.Created>(await CreateAsync(date, "National", "DE"));

        Assert.IsType<CreateHolidayOutcome.Created>(await CreateAsync(date, "National", "AT"));
        Assert.IsType<CreateHolidayOutcome.Created>(await CreateAsync(date, "Regional", "DE", "by"));
        Assert.IsType<CreateHolidayOutcome.Created>(await CreateAsync(date, "Regional", "DE", "BE"));
        Assert.IsType<CreateHolidayOutcome.AlreadyExists>(await CreateAsync(date, "Regional again", "DE", "BY"));
    }

    [Fact]
    public async Task List_filters_by_country_and_orders_by_date()
    {
        var later = WorkforceTestData.UniqueHolidayDate();
        var earlier = later.AddDays(-400);
        Assert.IsType<CreateHolidayOutcome.Created>(await CreateAsync(later, "Later", "CZ"));
        Assert.IsType<CreateHolidayOutcome.Created>(await CreateAsync(earlier, "Earlier", "CZ"));
        Assert.IsType<CreateHolidayOutcome.Created>(await CreateAsync(later, "Other country", "SK"));

        var czech = await ListAsync("cz");

        Assert.All(czech, holiday => Assert.Equal("CZ", holiday.CountryCode));
        var dates = czech.Select(holiday => holiday.Date).ToList();
        Assert.Equal(dates.Order(), dates);
        Assert.Contains(later, dates);
        Assert.Contains(earlier, dates);
        Assert.Contains(await ListAsync(null), holiday => holiday.CountryCode == "SK" && holiday.Date == later);
    }

    [Fact]
    public async Task List_rejects_an_invalid_country_filter()
    {
        await using var scope = database.CreateScope();

        var outcome = await scope.ServiceProvider.GetRequiredService<HolidayService>().ListAsync("POL", Cancellation);

        Assert.Equal(["countryCode"], Assert.IsType<ListHolidaysOutcome.Invalid>(outcome).Errors.Keys);
    }

    [Theory]
    [InlineData(false, "Name", "PL", null, "date")]
    [InlineData(true, null, "PL", null, "name")]
    [InlineData(true, "Name", null, null, "countryCode")]
    [InlineData(true, "Name", "XX", null, "countryCode")]
    [InlineData(true, "Name", "PL", "ABCD", "regionCode")]
    [InlineData(true, "Name", "PL", "M-Z", "regionCode")]
    public async Task Create_rejects_invalid_input(bool hasDate, string? name, string? countryCode, string? regionCode, string field)
    {
        var date = hasDate ? WorkforceTestData.UniqueHolidayDate() : (DateOnly?)null;

        var invalid = Assert.IsType<CreateHolidayOutcome.Invalid>(await CreateAsync(date, name, countryCode, regionCode));

        Assert.Equal([field], invalid.Errors.Keys);
    }
}
