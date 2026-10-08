extern alias api;
extern alias integrations;

using System.Net.Http.Json;
using System.Text.Json;
using CrewCall.Contracts.Reporting;
using CrewCall.Contracts.Visits;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CrewCall.Reporting.Tests;

/// <summary>
/// The whole read side, end to end (ADR-0016): business operations through CrewCall.Api → operational events and outbox
/// in one transaction → CrewCall.Integrations publishes to RabbitMQ → the reporting consumer projects into the reporting
/// database → the Reporting API answers. The three services share nothing but the broker.
/// </summary>
[Collection(Sequential.Name)]
public sealed class ReportingEndToEndTests(ReportingInfrastructure infrastructure) : IAsyncLifetime
{
    private readonly string _queue = ReportingHosts.NewQueueName();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await infrastructure.ResetAsync();

    public async ValueTask DisposeAsync() => await ReportingHosts.DeleteQueueAsync(infrastructure, _queue);

    [Fact]
    public async Task A_visit_assigned_executed_and_completed_through_the_api_appears_in_the_reports_with_its_actual_durations()
    {
        // Reporting first, so its queue is bound before anything is published.
        await using var reportingHost = ReportingHosts.Reporting(infrastructure, _queue);
        using var reporting = reportingHost.CreateClient();
        await using var apiHost = new WebApplicationFactory<api::Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:crewcall", infrastructure.OperationalConnectionString));
        using var api = apiHost.CreateClient(); // starts the API, which migrates the operational database
        await using var integrationsHost = new WebApplicationFactory<integrations::Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:crewcall", infrastructure.OperationalConnectionString)
            .UseSetting("ConnectionStrings:crewcall-rabbitmq", infrastructure.BrokerConnectionString)
            .UseSetting("Outbox:PollInterval", "00:00:00.200"));
        _ = integrationsHost.Services; // starts the outbox publisher

        // 1–2. A visit (Monday 2038-06-07, 09:00–10:00 in Warsaw) and its assignment.
        var technician = await PostAsync(api, "/api/technicians",
            new { displayName = "Reporting E2E", email = $"e2e-{Guid.NewGuid():N}@crewcall.test", timeZoneId = "Europe/Warsaw", countryCode = "PL" });
        await PostAsync(api, $"/api/technicians/{technician}/working-hours", new { dayOfWeek = "Monday", startLocalTime = "08:00", endLocalTime = "16:00" });
        var customer = await PostAsync(api, "/api/customers", new { name = $"Customer {Guid.NewGuid():N}" });
        var site = await PostAsync(api, "/api/sites", new { customerId = customer, name = "Plant 1", city = "Poznań", countryCode = "PL" });
        var workOrder = await PostAsync(api, "/api/work-orders", new { customerId = customer, siteId = site, title = "Replace inverter", priority = "High" });
        var visit = await PostAsync(api, $"/api/work-orders/{workOrder}/visits", new { start = "2038-06-07T07:00:00Z", end = "2038-06-07T08:00:00Z" });
        await PostAsync(api, $"/api/visits/{visit}/assignment", new { technicianId = technician }, idProperty: "assignmentId");

        // 3–4. Field work: travel, work, a pause, completion.
        foreach (var step in new[] { "start-travel", "start-work", "pause", "resume" })
        {
            (await api.PostAsync($"/api/visits/{visit}/execution/{step}", null, Cancellation)).EnsureSuccessStatusCode();
        }

        using var completion = await api.PostAsync($"/api/visits/{visit}/execution/complete", null, Cancellation);
        var metrics = (await completion.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<VisitExecutionResponse>(Cancellation))!.Metrics;

        // 5–8. Outbox → RabbitMQ → reporting consumer → Reporting API, eventually.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var report = await Wait.UntilAsync(
            async () => (await reporting.GetFromJsonAsync<TechnicianActivityReport>(
                $"/api/reporting/technicians/{technician}?start={today.AddDays(-1):yyyy-MM-dd}&end={today.AddDays(1):yyyy-MM-dd}", Cancellation))!,
            r => r.CompletedVisits == 1 && r.AssignmentCount == 1,
            "the technician report", TimeSpan.FromSeconds(60));

        Assert.Equal(
            (metrics.TravelMinutes!.Value, metrics.GrossWorkMinutes!.Value, metrics.PauseMinutes, metrics.NetWorkMinutes!.Value),
            (report.TravelMinutes, report.GrossWorkMinutes, report.PauseMinutes, report.NetWorkMinutes));
        Assert.NotNull(report.Metadata.DataAsOfUtc);

        var visits = await Wait.UntilAsync(
            async () => (await reporting.GetFromJsonAsync<VisitActivityReport>($"/api/reporting/visits?technicianId={technician}", Cancellation))!,
            r => r.Visits.Count == 1 && r.Visits[0].Status == "Completed", "the visit report");
        var projected = visits.Visits[0];
        Assert.Equal((visit, (Guid?)workOrder, (Guid?)customer, (Guid?)site), (projected.VisitId, projected.WorkOrderId, projected.CustomerId, projected.SiteId));
        Assert.Equal((60m, metrics.NetWorkMinutes, metrics.NetWorkMinutes - 60m), (projected.PlannedDurationMinutes, projected.ActualNetWorkMinutes, projected.VarianceMinutes));
    }

    private static async Task<Guid> PostAsync(HttpClient client, string url, object body, string idProperty = "id")
    {
        using var response = await client.PostAsJsonAsync(url, body, Cancellation);
        var json = await response.Content.ReadAsStringAsync(Cancellation);
        Assert.True(response.IsSuccessStatusCode, $"POST {url}: {(int)response.StatusCode} {json}");
        return JsonDocument.Parse(json).RootElement.GetProperty(idProperty).GetGuid();
    }
}
