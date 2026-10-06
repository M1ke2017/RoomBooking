using System.Net;
using System.Net.Http.Json;
using CrewCall.Contracts.Equipment;
using CrewCall.Contracts.OperationalCalendar;
using CrewCall.Contracts.Scheduling;
using CrewCall.Contracts.Sites;
using CrewCall.Contracts.Teams;
using CrewCall.Contracts.Vehicles;
using CrewCall.Contracts.Visits;
using CrewCall.Contracts.WorkingHours;
using CrewCall.Contracts.WorkOrders;
using CrewCall.Persistence.ReadModels.OperationalCalendar;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Api.Tests;

/// <summary>
/// The operational calendar end to end, on data created through the public API. Tests that use the All perspective each
/// get their own 40-day slot in 2030, so visits created by other tests in the shared database never fall into their range.
/// </summary>
public sealed class OperationalCalendarEndpointsTests(CrewCallApiFactory factory)
{
    private static int _slotSequence;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Midnight UTC at the start of a 40-day window no other test uses.</summary>
    private static DateTimeOffset NewSlot() =>
        new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(40 * Interlocked.Increment(ref _slotSequence));

    // ---------------------------------------------------------------- test data

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object request)
    {
        using var response = await client.PostAsJsonAsync(url, request, Cancellation);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Cancellation))!;
    }

    private static string UniqueCode(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20].ToUpperInvariant();

    private sealed record SiteRef(Guid CustomerId, Guid SiteId);

    private static async Task<SiteRef> CreateSiteAsync(HttpClient client)
    {
        var customerId = await ApiTestData.CreateCustomerAsync(client);
        return new SiteRef(customerId, await ApiTestData.CreateSiteAsync(client, customerId));
    }

    private static async Task<Guid> CreateVisitAsync(HttpClient client, DateTimeOffset start, DateTimeOffset end, SiteRef? site = null)
    {
        site ??= await CreateSiteAsync(client);
        var workOrder = await PostAsync<WorkOrderResponse>(client, "/api/work-orders",
            new CreateWorkOrderRequest(site.CustomerId, site.SiteId, "Inverter service", null, "High"));
        return (await PostAsync<VisitResponse>(client, $"/api/work-orders/{workOrder.Id}/visits", new CreateVisitRequest(start, end, null))).Id;
    }

    /// <summary>A Warsaw technician working every day 00:00–24:00, so any visit can be assigned.</summary>
    private static async Task<Guid> CreateTechnicianAsync(HttpClient client, Guid? teamId = null)
    {
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);
        foreach (var day in Enum.GetNames<DayOfWeek>())
        {
            await PostAsync<WorkingHoursResponse>(client, $"/api/technicians/{technicianId}/working-hours",
                new CreateWorkingHoursRequest(day, "00:00", "24:00"));
        }

        if (teamId is { } team)
        {
            (await client.PostAsync($"/api/teams/{team}/technicians/{technicianId}", null, Cancellation)).EnsureSuccessStatusCode();
        }

        return technicianId;
    }

    private static async Task<Guid> CreateVehicleAsync(HttpClient client) =>
        (await PostAsync<VehicleResponse>(client, "/api/vehicles", new CreateVehicleRequest(UniqueCode("WA"), "Service van", null, null))).Id;

    private static async Task<EquipmentResponse> CreateEquipmentAsync(HttpClient client, string name) =>
        await PostAsync<EquipmentResponse>(client, "/api/equipment", new CreateEquipmentRequest(name, UniqueCode("EQ"), null));

    private static Task<AssignmentResponse> AssignAsync(
        HttpClient client, Guid visitId, Guid technicianId, Guid? vehicleId = null, Guid[]? equipment = null, int? before = null, int? after = null) =>
        PostAsync<AssignmentResponse>(client, $"/api/visits/{visitId}/assignment",
            new CreateAssignmentRequest(technicianId, vehicleId, equipment, null, before, after));

    private static async Task ChangeStatusAsync(HttpClient client, Guid visitId, params string[] statuses)
    {
        foreach (var status in statuses)
        {
            (await client.PostAsJsonAsync($"/api/visits/{visitId}/status", new ChangeVisitStatusRequest(status), Cancellation)).EnsureSuccessStatusCode();
        }
    }

    private static string Url(DateTimeOffset start, DateTimeOffset end, string timeZoneId = "Europe/Warsaw", string? perspective = null, Guid? perspectiveId = null)
    {
        var url = $"/api/operational-calendar?start={Uri.EscapeDataString(start.ToString("O"))}&end={Uri.EscapeDataString(end.ToString("O"))}"
            + $"&timeZoneId={Uri.EscapeDataString(timeZoneId)}";
        if (perspective is not null)
        {
            url += $"&perspective={perspective}";
        }

        if (perspectiveId is not null)
        {
            url += $"&perspectiveId={perspectiveId}";
        }

        return url;
    }

    private static async Task<OperationalCalendarResponse> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OperationalCalendarResponse>(Cancellation))!;
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, Cancellation);
        return response.StatusCode;
    }

    private static Guid[] Ids(OperationalCalendarResponse calendar) => calendar.Items.Select(item => item.VisitId).ToArray();

    // ---------------------------------------------------------------- range

    [Fact]
    public async Task Visits_overlapping_the_half_open_range_are_included_and_touching_ones_excluded()
    {
        using var client = factory.CreateClient();
        var slot = NewSlot();
        DateTimeOffset At(int hour, int minute = 0) => slot.AddHours(hour).AddMinutes(minute);
        var inside = await CreateVisitAsync(client, At(10, 15), At(11, 15));
        var startsBefore = await CreateVisitAsync(client, At(9, 30), At(10, 30));
        var endsAfter = await CreateVisitAsync(client, At(11, 30), At(13));
        var touchesStart = await CreateVisitAsync(client, At(9), At(10));
        var touchesEnd = await CreateVisitAsync(client, At(12), At(13));
        var outside = await CreateVisitAsync(client, At(15), At(16));

        var calendar = await GetAsync(client, Url(At(10), At(12)));

        Assert.Equal([startsBefore, inside, endsAfter], Ids(calendar));
        Assert.Equal(3, calendar.TotalCount);
        Assert.DoesNotContain(touchesStart, Ids(calendar));
        Assert.DoesNotContain(touchesEnd, Ids(calendar));
        Assert.DoesNotContain(outside, Ids(calendar));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(31)]
    public async Task Day_week_and_month_ranges_are_served_by_the_same_endpoint(int days)
    {
        using var client = factory.CreateClient();
        var slot = NewSlot();
        var first = await CreateVisitAsync(client, slot.AddHours(8), slot.AddHours(9));
        var last = await CreateVisitAsync(client, slot.AddDays(days).AddHours(-2), slot.AddDays(days).AddHours(-1));
        var after = await CreateVisitAsync(client, slot.AddDays(days).AddHours(1), slot.AddDays(days).AddHours(2));

        var calendar = await GetAsync(client, Url(slot, slot.AddDays(days)));

        Assert.Equal([first, last], Ids(calendar));
        Assert.DoesNotContain(after, Ids(calendar));
    }

    [Theory]
    [InlineData(0, 0, "end")]    // start == end
    [InlineData(2, 1, "end")]    // start after end
    [InlineData(0, 32, "end")]   // more than 31 days
    public async Task Invalid_ranges_return_400(int startDays, int endDays, string field)
    {
        using var client = factory.CreateClient();
        var slot = NewSlot();

        using var response = await client.GetAsync(Url(slot.AddDays(startDays), slot.AddDays(endDays)), Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync(Cancellation));
    }

    [Fact]
    public async Task Invalid_query_parameters_return_400()
    {
        using var client = factory.CreateClient();
        var slot = NewSlot();

        Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, Url(slot, slot.AddDays(1), timeZoneId: "Mars/Olympus")));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, Url(slot, slot.AddDays(1), timeZoneId: "Central European Standard Time")));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, Url(slot, slot.AddDays(1), perspective: "customer", perspectiveId: Guid.NewGuid())));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, Url(slot, slot.AddDays(1), perspective: "technician")));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, Url(slot, slot.AddDays(1), perspective: "all", perspectiveId: Guid.NewGuid())));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, "/api/operational-calendar?start=2030-01-01T00:00:00&end=2030-01-02T00:00:00Z&timeZoneId=UTC"));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, "/api/operational-calendar?start=2030-01-01T00:00:00Z&end=2030-01-02T00:00:00Z"));
    }

    // ---------------------------------------------------------------- All

    [Fact]
    public async Task All_returns_assigned_unassigned_completed_and_cancelled_visits_with_full_details()
    {
        using var client = factory.CreateClient();
        var slot = NewSlot();
        var teamId = await ApiTestData.CreateTeamAsync(client);
        var technicianId = await CreateTechnicianAsync(client, teamId);
        var vehicleId = await CreateVehicleAsync(client);
        var drill = await CreateEquipmentAsync(client, "Drill");
        var analyzer = await CreateEquipmentAsync(client, "Analyzer");
        var site = await CreateSiteAsync(client);

        var assigned = await CreateVisitAsync(client, slot.AddHours(8), slot.AddHours(9), site);
        var assignment = await AssignAsync(client, assigned, technicianId, vehicleId, [drill.Id, analyzer.Id], before: 15, after: 30);
        var unassigned = await CreateVisitAsync(client, slot.AddHours(10), slot.AddHours(11));
        var completed = await CreateVisitAsync(client, slot.AddHours(12), slot.AddHours(13));
        await ChangeStatusAsync(client, completed, "InProgress", "Completed");
        var cancelled = await CreateVisitAsync(client, slot.AddHours(14), slot.AddHours(15));
        await ChangeStatusAsync(client, cancelled, "Cancelled");

        var calendar = await GetAsync(client, Url(slot, slot.AddDays(1)));

        Assert.Equal([assigned, unassigned, completed, cancelled], Ids(calendar));
        Assert.Equal(("All", (Guid?)null, "Europe/Warsaw"), (calendar.Perspective, calendar.PerspectiveId, calendar.TimeZoneId));

        var item = calendar.Items[0];
        Assert.Equal((site.CustomerId, site.SiteId, "Plant 1", "Poznań"), (item.CustomerId, item.SiteId, item.SiteName, item.SiteCity));
        Assert.StartsWith("Customer ", item.CustomerName);
        Assert.Equal(("Inverter service", "High", "Open"), (item.WorkOrderTitle, item.WorkOrderPriority, item.WorkOrderStatus));
        Assert.Equal(("Planned", assignment.AssignmentId, "Active"), (item.VisitStatus, item.AssignmentId, item.AssignmentStatus));
        Assert.Equal((technicianId, "Technician", teamId), (item.TechnicianId!.Value, item.TechnicianName, item.TeamId!.Value));
        Assert.StartsWith("Team ", item.TeamName);
        Assert.Equal((vehicleId, "Service van"), (item.VehicleId!.Value, item.VehicleName));
        Assert.StartsWith("WA-", item.VehicleRegistrationNumber);
        Assert.Equal((15, 30), (item.TravelBufferBeforeMinutes!.Value, item.TravelBufferAfterMinutes!.Value));
        Assert.Equal(
            [new OperationalCalendarEquipmentResponse(analyzer.Id, "Analyzer", analyzer.AssetCode), new OperationalCalendarEquipmentResponse(drill.Id, "Drill", drill.AssetCode)],
            item.Equipment);

        var open = calendar.Items[1];
        Assert.Null(open.AssignmentId);
        Assert.Null(open.AssignmentStatus);
        Assert.Null(open.TechnicianId);
        Assert.Null(open.VehicleId);
        Assert.Null(open.TravelBufferBeforeMinutes);
        Assert.Empty(open.Equipment);
        Assert.Equal(["Planned", "Planned", "Completed", "Cancelled"], calendar.Items.Select(i => i.VisitStatus));
    }

    [Fact]
    public async Task Ordering_is_by_start_then_end_then_visit_id()
    {
        using var client = factory.CreateClient();
        var slot = NewSlot();
        var created = new List<(Guid Id, DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var (startHour, endHour) in new[] { (10, 12), (8, 9), (10, 11), (10, 11), (10, 11) })
        {
            var start = slot.AddHours(startHour);
            var end = slot.AddHours(endHour);
            created.Add((await CreateVisitAsync(client, start, end), start, end));
        }

        var expected = created.OrderBy(v => v.Start).ThenBy(v => v.End).ThenBy(v => v.Id).Select(v => v.Id).ToArray();

        Assert.Equal(expected, Ids(await GetAsync(client, Url(slot, slot.AddDays(1)))));
        Assert.Equal(expected, Ids(await GetAsync(client, Url(slot, slot.AddDays(1)))));
    }

    [Fact]
    public async Task Only_the_current_assignment_is_shown_after_a_reassignment()
    {
        using var client = factory.CreateClient();
        var slot = NewSlot();
        var visitId = await CreateVisitAsync(client, slot.AddHours(8), slot.AddHours(9));
        await AssignAsync(client, visitId, await CreateTechnicianAsync(client));
        var newTechnician = await CreateTechnicianAsync(client);
        var replacement = await PostAsync<AssignmentResponse>(client, $"/api/visits/{visitId}/assignment/reassign",
            new ReassignAssignmentRequest(newTechnician, null, null, null, null, null));

        var item = Assert.Single((await GetAsync(client, Url(slot, slot.AddDays(1)))).Items);

        Assert.Equal((replacement.AssignmentId, newTechnician), (item.AssignmentId!.Value, item.TechnicianId!.Value));
    }

    // ---------------------------------------------------------------- Technician

    [Fact]
    public async Task Technician_perspective_returns_only_that_technicians_assigned_visits()
    {
        using var client = factory.CreateClient();
        var slot = NewSlot();
        var technicianId = await CreateTechnicianAsync(client);
        var otherTechnician = await CreateTechnicianAsync(client);
        var mine = await CreateVisitAsync(client, slot.AddHours(8), slot.AddHours(9));
        await AssignAsync(client, mine, technicianId);
        var theirs = await CreateVisitAsync(client, slot.AddHours(8), slot.AddHours(9));
        await AssignAsync(client, theirs, otherTechnician);
        var unassigned = await CreateVisitAsync(client, slot.AddHours(10), slot.AddHours(11));

        var calendar = await GetAsync(client, Url(slot, slot.AddDays(1), perspective: "technician", perspectiveId: technicianId));

        Assert.Equal([mine], Ids(calendar));
        Assert.Equal(("Technician", technicianId), (calendar.Perspective, calendar.PerspectiveId!.Value));
        Assert.DoesNotContain(unassigned, Ids(calendar));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, Url(slot, slot.AddDays(1), perspective: "Technician", perspectiveId: Guid.NewGuid())));
    }

    // ---------------------------------------------------------------- Team

    [Fact]
    public async Task Team_perspective_uses_the_technicians_current_team()
    {
        using var client = factory.CreateClient();
        var slot = NewSlot();
        var teamId = await ApiTestData.CreateTeamAsync(client);
        var otherTeamId = await ApiTestData.CreateTeamAsync(client);
        var member = await CreateTechnicianAsync(client, teamId);
        var otherMember = await CreateTechnicianAsync(client, otherTeamId);
        var noTeam = await CreateTechnicianAsync(client);

        var teamVisit = await CreateVisitAsync(client, slot.AddHours(8), slot.AddHours(9));
        await AssignAsync(client, teamVisit, member);
        var otherTeamVisit = await CreateVisitAsync(client, slot.AddHours(8), slot.AddHours(9));
        await AssignAsync(client, otherTeamVisit, otherMember);
        var noTeamVisit = await CreateVisitAsync(client, slot.AddHours(8), slot.AddHours(9));
        await AssignAsync(client, noTeamVisit, noTeam);

        Assert.Equal([teamVisit], Ids(await GetAsync(client, Url(slot, slot.AddDays(1), perspective: "team", perspectiveId: teamId))));

        // Current membership, not historical: after the member moves, the visit follows the new team.
        (await client.DeleteAsync($"/api/teams/{teamId}/technicians/{member}", Cancellation)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/teams/{otherTeamId}/technicians/{member}", null, Cancellation)).EnsureSuccessStatusCode();

        Assert.Empty((await GetAsync(client, Url(slot, slot.AddDays(1), perspective: "team", perspectiveId: teamId))).Items);
        Assert.Equal(
            new[] { teamVisit, otherTeamVisit }.Order(),
            Ids(await GetAsync(client, Url(slot, slot.AddDays(1), perspective: "team", perspectiveId: otherTeamId))).Order());
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, Url(slot, slot.AddDays(1), perspective: "team", perspectiveId: Guid.NewGuid())));
    }

    // ---------------------------------------------------------------- Vehicle

    [Fact]
    public async Task Vehicle_perspective_returns_visits_whose_assignment_uses_the_vehicle()
    {
        using var client = factory.CreateClient();
        var slot = NewSlot();
        var vehicleId = await CreateVehicleAsync(client);
        var otherVehicle = await CreateVehicleAsync(client);
        var withVehicle = await CreateVisitAsync(client, slot.AddHours(8), slot.AddHours(9));
        await AssignAsync(client, withVehicle, await CreateTechnicianAsync(client), vehicleId);
        var withOtherVehicle = await CreateVisitAsync(client, slot.AddHours(8), slot.AddHours(9));
        await AssignAsync(client, withOtherVehicle, await CreateTechnicianAsync(client), otherVehicle);
        var withoutVehicle = await CreateVisitAsync(client, slot.AddHours(8), slot.AddHours(9));
        await AssignAsync(client, withoutVehicle, await CreateTechnicianAsync(client));
        await CreateVisitAsync(client, slot.AddHours(10), slot.AddHours(11)); // unassigned

        var calendar = await GetAsync(client, Url(slot, slot.AddDays(1), perspective: "vehicle", perspectiveId: vehicleId));

        Assert.Equal([withVehicle], Ids(calendar));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, Url(slot, slot.AddDays(1), perspective: "vehicle", perspectiveId: Guid.NewGuid())));
    }

    // ---------------------------------------------------------------- Site

    [Fact]
    public async Task Site_perspective_returns_the_sites_visits_including_unassigned_ones()
    {
        using var client = factory.CreateClient();
        var slot = NewSlot();
        var site = await CreateSiteAsync(client);
        var assigned = await CreateVisitAsync(client, slot.AddHours(8), slot.AddHours(9), site);
        await AssignAsync(client, assigned, await CreateTechnicianAsync(client));
        var unassigned = await CreateVisitAsync(client, slot.AddHours(10), slot.AddHours(11), site);
        var elsewhere = await CreateVisitAsync(client, slot.AddHours(8), slot.AddHours(9));

        var calendar = await GetAsync(client, Url(slot, slot.AddDays(1), perspective: "site", perspectiveId: site.SiteId));

        Assert.Equal([assigned, unassigned], Ids(calendar));
        Assert.DoesNotContain(elsewhere, Ids(calendar));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, Url(slot, slot.AddDays(1), perspective: "site", perspectiveId: Guid.NewGuid())));
    }

    // ---------------------------------------------------------------- time zones

    [Fact]
    public async Task Local_times_are_projected_into_the_requested_zone_with_UTC_kept()
    {
        using var client = factory.CreateClient();
        var site = await CreateSiteAsync(client);
        var start = new DateTimeOffset(2026, 7, 6, 8, 0, 0, TimeSpan.Zero);
        var visitId = await CreateVisitAsync(client, start, start.AddHours(1), site);
        var range = (start.AddHours(-1), start.AddHours(2));

        var warsaw = Assert.Single((await GetAsync(client, Url(range.Item1, range.Item2, "Europe/Warsaw", "site", site.SiteId))).Items);
        var newYork = await GetAsync(client, Url(range.Item1, range.Item2, "America/New_York", "site", site.SiteId));

        Assert.Equal(visitId, warsaw.VisitId);
        Assert.Equal((start, TimeSpan.Zero), (warsaw.VisitStartUtc, warsaw.VisitStartUtc.Offset));
        Assert.Equal(new DateTimeOffset(2026, 7, 6, 10, 0, 0, TimeSpan.FromHours(2)), warsaw.VisitLocalStart);
        Assert.Equal(TimeSpan.FromHours(2), warsaw.VisitLocalStart.Offset);
        var nyItem = Assert.Single(newYork.Items);
        Assert.Equal(new DateTimeOffset(2026, 7, 6, 4, 0, 0, TimeSpan.FromHours(-4)), nyItem.VisitLocalStart);
        Assert.Equal(TimeSpan.FromHours(-4), nyItem.VisitLocalStart.Offset);
        Assert.Equal("America/New_York", newYork.TimeZoneId);
        Assert.Equal(TimeSpan.FromHours(-4), newYork.RangeLocalStart.Offset);
        Assert.Equal(range.Item1, newYork.RangeStartUtc);
    }

    [Fact]
    public async Task Local_times_follow_DST_changes()
    {
        using var client = factory.CreateClient();
        var site = await CreateSiteAsync(client);

        // Spring forward in Warsaw, 2026-03-29: 00:30Z is 01:30 CET, 01:30Z is 03:30 CEST.
        var spring = new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero);
        await CreateVisitAsync(client, spring, spring.AddHours(1), site);

        // Fall back, 2026-10-25: 00:30Z and 01:30Z are both 02:30 local, first CEST then CET.
        var fall = new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero);
        await CreateVisitAsync(client, fall, fall.AddHours(1), site);

        var springItem = Assert.Single((await GetAsync(client, Url(spring.AddHours(-2), spring.AddHours(3), "Europe/Warsaw", "site", site.SiteId))).Items);
        var fallItem = Assert.Single((await GetAsync(client, Url(fall.AddHours(-2), fall.AddHours(3), "Europe/Warsaw", "site", site.SiteId))).Items);

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.FromHours(1)), springItem.VisitLocalStart);
        Assert.Equal(TimeSpan.FromHours(1), springItem.VisitLocalStart.Offset);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 3, 30, 0, TimeSpan.FromHours(2)), springItem.VisitLocalEnd);
        Assert.Equal(TimeSpan.FromHours(2), springItem.VisitLocalEnd.Offset);

        Assert.Equal((2, 30, TimeSpan.FromHours(2)), (fallItem.VisitLocalStart.Hour, fallItem.VisitLocalStart.Minute, fallItem.VisitLocalStart.Offset));
        Assert.Equal((2, 30, TimeSpan.FromHours(1)), (fallItem.VisitLocalEnd.Hour, fallItem.VisitLocalEnd.Minute, fallItem.VisitLocalEnd.Offset));
    }

    // ---------------------------------------------------------------- performance guard (N+1)

    [Fact]
    public async Task The_number_of_SQL_queries_does_not_grow_with_the_number_of_visits()
    {
        using var client = factory.CreateClient();
        var technicianId = await CreateTechnicianAsync(client);
        var vehicleId = await CreateVehicleAsync(client);
        var drill = await CreateEquipmentAsync(client, "Drill");
        var analyzer = await CreateEquipmentAsync(client, "Analyzer");

        async Task<DateTimeOffset> SlotWithAssignedVisitsAsync(int count)
        {
            var slot = NewSlot();
            for (var index = 0; index < count; index++)
            {
                var visitId = await CreateVisitAsync(client, slot.AddHours(index), slot.AddHours(index + 1));
                await AssignAsync(client, visitId, technicianId, vehicleId, [drill.Id, analyzer.Id]);
            }

            return slot;
        }

        async Task<(int Queries, int Items)> MeasureAsync(DateTimeOffset slot, string? perspective, Guid? perspectiveId)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<OperationalCalendarService>();
            OperationalCalendarOutcome? outcome = null;
            var queries = await SqlCommandCounter.CountAsync(async () =>
                outcome = await service.GetAsync(
                    new OperationalCalendarQuery(slot, slot.AddDays(1), perspective, perspectiveId, "Europe/Warsaw"), Cancellation));
            return (queries, Assert.IsType<OperationalCalendarOutcome.Listed>(outcome).Calendar.Items.Count);
        }

        var small = await SlotWithAssignedVisitsAsync(1);
        var large = await SlotWithAssignedVisitsAsync(12);

        var (smallQueries, smallItems) = await MeasureAsync(small, null, null);
        var (largeQueries, largeItems) = await MeasureAsync(large, null, null);
        var (technicianQueries, technicianItems) = await MeasureAsync(large, "technician", technicianId);

        Assert.Equal((1, 12, 12), (smallItems, largeItems, technicianItems));
        Assert.Equal(smallQueries, largeQueries);
        Assert.Equal(2, largeQueries);      // visits (with joins) + equipment
        Assert.Equal(3, technicianQueries); // + the perspective existence check
    }
}
