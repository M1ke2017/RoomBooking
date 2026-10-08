extern alias api;
extern alias integrations;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using CrewCall.Contracts.Incidents;
using CrewCall.Contracts.Live;
using CrewCall.Contracts.OperationalCalendar;
using CrewCall.Contracts.Operations;
using CrewCall.Contracts.Reporting;
using CrewCall.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Reporting.Tests;

/// <summary>
/// Sprint 15's main scenario, end to end across the three services (ADR-0017): an urgent incident whose best technician
/// is busy → a reschedule proposal → the manager applies it → one transaction moves the lower-priority visit and
/// dispatches the incident → the calendar shows the new plan → history and outbox carry visit.rescheduled → the live
/// SignalR client is told → the reporting read side counts the reschedule and its delay.
/// </summary>
[Collection(Sequential.Name)]
public sealed class RescheduleEndToEndTests(ReportingInfrastructure infrastructure) : IAsyncLifetime
{
    private static readonly DateTimeOffset Monday = new(2038, 6, 7, 0, 0, 0, TimeSpan.Zero);

    private readonly string _reportingQueue = ReportingHosts.NewQueueName();
    private readonly string _liveQueue = $"crewcall.live-operations.test-{Guid.NewGuid():N}";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await infrastructure.ResetAsync();

    public async ValueTask DisposeAsync()
    {
        await ReportingHosts.DeleteQueueAsync(infrastructure, _reportingQueue);
        await ReportingHosts.DeleteQueueAsync(infrastructure, _liveQueue);
    }

    [Fact]
    public async Task An_urgent_incident_moves_one_lower_priority_visit_and_every_service_sees_the_new_plan()
    {
        // The three services: reporting first (its queue is bound before anything is published), the API, then
        // Integrations (outbox publisher, live consumer, SignalR hub).
        await using var reportingHost = ReportingHosts.Reporting(infrastructure, _reportingQueue);
        using var reporting = reportingHost.CreateClient();
        await using var apiHost = new WebApplicationFactory<api::Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:crewcall", infrastructure.OperationalConnectionString));
        using var api = apiHost.CreateClient();
        await using var integrationsHost = new WebApplicationFactory<integrations::Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:crewcall", infrastructure.OperationalConnectionString)
            .UseSetting("ConnectionStrings:crewcall-rabbitmq", infrastructure.BrokerConnectionString)
            .UseSetting("Outbox:PollInterval", "00:00:00.200")
            .UseSetting("LiveOperations:QueueName", _liveQueue)
            .UseSetting("LiveOperations:ReconnectDelay", "00:00:00.200"));
        _ = integrationsHost.Server; // starts the outbox publisher and the live consumer

        // The plan: technician A has X 09:00–10:00 (Normal) and Y 10:30–12:00 (Normal, 30 min travel before).
        var a = await PostAsync(api, "/api/technicians",
            new { displayName = "Anna Nowak", email = $"anna-{Guid.NewGuid():N}@crewcall.test", timeZoneId = "UTC", countryCode = "PL", phoneNumber = "+48 601 234 567" });
        await PostAsync(api, $"/api/technicians/{a}/working-hours", new { dayOfWeek = "Monday", startLocalTime = "07:00", endLocalTime = "18:00" });
        var x = await PlannedVisitAsync(api, a, Monday.AddHours(9), Monday.AddHours(10), travelBefore: 0);
        var y = await PlannedVisitAsync(api, a, Monday.AddHours(10.5), Monday.AddHours(12), travelBefore: 30);

        // The urgent incident Z (Critical) for 11:30–13:00, at another customer's site.
        var customer = await PostAsync(api, "/api/customers", new { name = $"Hospital {Guid.NewGuid():N}" });
        var site = await PostAsync(api, "/api/sites", new { customerId = customer, name = "Server room", city = "Poznań", countryCode = "PL" });
        var incident = await PostAsync(api, "/api/incidents", new
        {
            customerId = customer, siteId = site, title = "Power outage", description = "Main switchboard down", priority = "Critical",
            requestedStart = Monday.AddHours(11.5), requestedEnd = Monday.AddHours(13), requiredSkillCodes = Array.Empty<string>()
        });
        (await api.PostAsJsonAsync($"/api/incidents/{incident}/analyze", new { candidateTechnicianIds = new[] { a } }, Cancellation)).EnsureSuccessStatusCode();

        // Proposal: A, moving Y to 14:00–15:30; 1 visit, 1 customer, 210 minutes.
        var proposal = Assert.Single(await PostAsync<RescheduleProposal[]>(api, $"/api/incidents/{incident}/reschedule-proposal",
            new RescheduleProposalRequest([a], null, null, null, 30)));
        Assert.Equal((a, (Guid?)y.Visit, true), (proposal.RecommendedTechnicianId, proposal.ConflictingVisitId, proposal.IsFeasible));
        Assert.Equal((Monday.AddHours(14), Monday.AddHours(15.5)), (proposal.ProposedNewStartUtc!.Value, proposal.ProposedNewEndUtc!.Value));
        Assert.Equal((1, 1, 210), (proposal.Impact.AffectedVisitCount, proposal.Impact.AffectedCustomerCount, proposal.Impact.DelayMinutes));

        // A dispatcher watches technician A live.
        await using var live = await LiveClient.ConnectAsync(integrationsHost);
        await live.Connection.InvokeAsync(LiveOperationsHubContract.SubscribeTechnician, a, Cancellation);

        // The manager approves.
        using var applied = await api.PostAsJsonAsync($"/api/incidents/{incident}/reschedule-proposal/apply",
            new ApplyRescheduleRequest(proposal.RecommendedTechnicianId, proposal.VehicleId, proposal.EquipmentIds,
                proposal.TravelBufferBeforeMinutes, proposal.TravelBufferAfterMinutes,
                proposal.ConflictingVisitId, proposal.ProposedNewStartUtc, proposal.ProposedNewEndUtc),
            Cancellation);
        Assert.Equal(HttpStatusCode.Created, applied.StatusCode);
        var result = (await applied.Content.ReadFromJsonAsync<ApplyRescheduleResponse>(Cancellation))!;
        Assert.Equal(("Dispatched", 210), (result.Dispatch.Incident.Status, result.MovedVisit!.DelayMinutes));

        // Calendar: X, the urgent visit, Y in its new slot (same visit, same assignment).
        var calendar = (await api.GetFromJsonAsync<OperationalCalendarResponse>(
            $"/api/operational-calendar?start=2038-06-07T00:00:00Z&end=2038-06-08T00:00:00Z&timeZoneId=UTC&perspective=technician&perspectiveId={a}",
            Cancellation))!;
        Assert.Equal(
            [
                (x.Visit, Monday.AddHours(9), (Guid?)x.Assignment),
                (result.Dispatch.VisitId, Monday.AddHours(11.5), (Guid?)result.Dispatch.Assignment.AssignmentId),
                (y.Visit, Monday.AddHours(14), (Guid?)y.Assignment)
            ],
            calendar.Items.Select(item => (item.VisitId, item.VisitStartUtc, item.AssignmentId)));

        // History: VisitRescheduled on Y; RescheduleApplied and IncidentDispatched on the incident.
        var visitHistory = (await api.GetFromJsonAsync<OperationalEventResponse[]>($"/api/operations/events/visit/{y.Visit}", Cancellation))!;
        Assert.Contains(visitHistory, e => e.EventType == "VisitRescheduled");
        var incidentHistory = (await api.GetFromJsonAsync<OperationalEventResponse[]>($"/api/operations/events/incident/{incident}", Cancellation))!;
        Assert.Equal(["IncidentCreated", "IncidentAnalyzed", "RescheduleApplied", "IncidentDispatched"], incidentHistory.Select(e => e.EventType));

        // Integration event → RabbitMQ → live consumer → SignalR: the client is told that Y moved (MessageId = outbox id).
        var outboxId = await OutboxIdAsync(apiHost, y.Visit);
        var message = await live.NextAsync(m => m.Type == LiveOperationTypes.VisitRescheduled);
        Assert.Equal((outboxId, y.Visit), (message.MessageId, message.EntityId));

        // Reporting: Y counted as rescheduled once, 210 minutes later, in its new window.
        var visits = await Wait.UntilAsync(
            async () => (await reporting.GetFromJsonAsync<VisitActivityReport>($"/api/reporting/visits?technicianId={a}", Cancellation))!,
            r => r.Visits.Any(v => v.VisitId == y.Visit && v.RescheduleCount == 1), "the rescheduled visit in the report", TimeSpan.FromSeconds(60));
        var reported = visits.Visits.Single(v => v.VisitId == y.Visit);
        Assert.Equal((1, 210, Monday.AddHours(14), Monday.AddHours(15.5)),
            (reported.RescheduleCount, reported.TotalDelayMinutes, reported.PlannedStartUtc!.Value, reported.PlannedEndUtc!.Value));
        Assert.Equal(0, visits.Visits.Single(v => v.VisitId == x.Visit).RescheduleCount);
    }

    private async Task<(Guid Visit, Guid Assignment)> PlannedVisitAsync(
        HttpClient api, Guid technician, DateTimeOffset start, DateTimeOffset end, int travelBefore)
    {
        var customer = await PostAsync(api, "/api/customers", new { name = $"Customer {Guid.NewGuid():N}" });
        var site = await PostAsync(api, "/api/sites", new { customerId = customer, name = "Plant 1", city = "Poznań", countryCode = "PL" });
        var workOrder = await PostAsync(api, "/api/work-orders", new { customerId = customer, siteId = site, title = "Inspection", priority = "Normal" });
        var visit = await PostAsync(api, $"/api/work-orders/{workOrder}/visits", new { start, end });
        var assignment = await PostAsync(api, $"/api/visits/{visit}/assignment",
            new { technicianId = technician, travelBufferBeforeMinutes = travelBefore }, idProperty: "assignmentId");
        return (visit, assignment);
    }

    private static async Task<Guid> OutboxIdAsync(WebApplicationFactory<api::Program> apiHost, Guid visitId)
    {
        await using var scope = apiHost.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().OutboxMessages
            .Where(m => m.Type == "visit.rescheduled" && m.AggregateId == visitId)
            .Select(m => m.Id)
            .SingleAsync(Cancellation);
    }

    private static async Task<Guid> PostAsync(HttpClient client, string url, object body, string idProperty = "id")
    {
        using var response = await client.PostAsJsonAsync(url, body, Cancellation);
        var json = await response.Content.ReadAsStringAsync(Cancellation);
        Assert.True(response.IsSuccessStatusCode, $"POST {url}: {(int)response.StatusCode} {json}");
        return JsonDocument.Parse(json).RootElement.GetProperty(idProperty).GetGuid();
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        using var response = await client.PostAsJsonAsync(url, body, Cancellation);
        Assert.True(response.IsSuccessStatusCode, $"POST {url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Cancellation)}");
        return (await response.Content.ReadFromJsonAsync<T>(Cancellation))!;
    }

    /// <summary>A SignalR client of the Integrations host (in-memory TestServer, so long polling).</summary>
    private sealed class LiveClient : IAsyncDisposable
    {
        private readonly Channel<LiveOperationMessage> _received = Channel.CreateUnbounded<LiveOperationMessage>();

        private LiveClient(HubConnection connection)
        {
            Connection = connection;
            Connection.On<LiveOperationMessage>(LiveOperationsHubContract.ReceiveOperation, message => _received.Writer.TryWrite(message));
        }

        public HubConnection Connection { get; }

        public static async Task<LiveClient> ConnectAsync(WebApplicationFactory<integrations::Program> host)
        {
            var connection = new HubConnectionBuilder()
                .WithUrl(new Uri(host.Server.BaseAddress, LiveOperationsHubContract.Path), options =>
                {
                    options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                })
                .Build();
            var client = new LiveClient(connection);
            await connection.StartAsync(Cancellation);
            return client;
        }

        /// <summary>The first message received that matches, within a minute.</summary>
        public async Task<LiveOperationMessage> NextAsync(Func<LiveOperationMessage, bool> match)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            while (true)
            {
                var message = await _received.Reader.ReadAsync(timeout.Token);
                if (match(message))
                {
                    return message;
                }
            }
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }
}
