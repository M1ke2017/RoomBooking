using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using CrewCall.Contracts.Incidents;
using CrewCall.Contracts.Scheduling;
using CrewCall.Contracts.Visits;
using CrewCall.Persistence;
using CrewCall.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CrewCall.Api.Tests.IncidentTestKit;

namespace CrewCall.Api.Tests;

/// <summary>
/// The transactional outbox on the write side (ADR-0014): published integration events are written by the same
/// SaveChanges as the business change, so both commit or neither does. No broker is involved here at all.
/// </summary>
public sealed class OutboxAtomicityTests(CrewCallApiFactory factory)
{
    private static int _daySequence;

    private static DateTimeOffset NewOutboxDay() =>
        new DateTimeOffset(2038, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(Interlocked.Increment(ref _daySequence));

    private async Task<List<OutboxMessage>> OutboxForAsync(Guid aggregateId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().OutboxMessages
            .AsNoTracking()
            .Where(message => message.AggregateId == aggregateId)
            .OrderBy(message => message.CreatedAtUtc)
            .ThenBy(message => message.Id)
            .ToListAsync(Cancellation);
    }

    private async Task<int> OutboxMentioningAsync(string type, string field, Guid value)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().OutboxMessages
            .CountAsync(message => message.Type == type && EF.Functions.JsonContains(message.Payload, $"{{\"{field}\":\"{value}\"}}"), Cancellation);
    }

    private static JsonElement Payload(OutboxMessage message) => JsonDocument.Parse(message.Payload).RootElement;

    private static Task<HttpResponseMessage> AssignAsync(HttpClient client, Guid visitId, Guid technicianId) =>
        client.PostAsJsonAsync($"/api/visits/{visitId}/assignment", new CreateAssignmentRequest(technicianId, null, null, null, null, null), Cancellation);

    private static async Task<VisitResponse> NewVisitAsync(HttpClient client, DateTimeOffset start)
    {
        var workOrder = await ApiTestData.CreateWorkOrderAsync(client);
        return await PostAsync<VisitResponse>(client, $"/api/work-orders/{workOrder.Id}/visits", new CreateVisitRequest(start, start.AddHours(1), null));
    }

    [Fact]
    public async Task Assignment_created_replaced_and_cancelled_each_write_their_outbox_message()
    {
        using var client = factory.CreateClient();
        var day = NewOutboxDay();
        var first = await CreateTechnicianAsync(client);
        var second = await CreateTechnicianAsync(client);
        var visit = await NewVisitAsync(client, day.AddHours(9));

        var created = await PostAsync<AssignmentResponse>(client, $"/api/visits/{visit.Id}/assignment",
            new CreateAssignmentRequest(first, null, null, null, null, null));
        var replaced = await PostAsync<AssignmentResponse>(client, $"/api/visits/{visit.Id}/assignment/reassign",
            new ReassignAssignmentRequest(second, null, null, null, null, null));
        (await client.DeleteAsync($"/api/visits/{visit.Id}/assignment", Cancellation)).EnsureSuccessStatusCode();

        var createdMessage = Assert.Single(await OutboxForAsync(created.AssignmentId), m => m.Type == "assignment.created");
        Assert.Equal((1, "assignment.created", "assignment"), (createdMessage.Version, createdMessage.RoutingKey, createdMessage.AggregateType));
        var createdPayload = Payload(createdMessage);
        Assert.Equal((created.AssignmentId, visit.Id, first),
            (createdPayload.GetProperty("assignmentId").GetGuid(), createdPayload.GetProperty("visitId").GetGuid(), createdPayload.GetProperty("technicianId").GetGuid()));
        Assert.Equal(createdMessage.Id, createdPayload.GetProperty("eventId").GetGuid());
        Assert.Null(createdMessage.ProcessedAtUtc);
        Assert.Equal(0, createdMessage.AttemptCount);

        var replacedMessage = Assert.Single(await OutboxForAsync(created.AssignmentId), m => m.Type == "assignment.replaced");
        var replacedPayload = Payload(replacedMessage);
        Assert.Equal((created.AssignmentId, replaced.AssignmentId, visit.Id),
            (replacedPayload.GetProperty("oldAssignmentId").GetGuid(), replacedPayload.GetProperty("newAssignmentId").GetGuid(), replacedPayload.GetProperty("visitId").GetGuid()));

        var replacementMessages = await OutboxForAsync(replaced.AssignmentId);
        Assert.Equal(["assignment.created", "assignment.cancelled"], replacementMessages.Select(m => m.Type));
        Assert.Equal(replaced.AssignmentId, Payload(replacementMessages[1]).GetProperty("assignmentId").GetGuid());
    }

    [Fact]
    public async Task Incident_dispatch_and_visit_work_completion_write_their_outbox_messages_and_internal_history_stays_internal()
    {
        using var client = factory.CreateClient();
        var day = NewOutboxDay();
        var technician = await CreateTechnicianAsync(client);
        var incident = await ReadyIncidentAsync(client, day, [technician]);
        using var dispatchResponse = await DispatchAsync(client, incident.Id, Dispatch(technician));
        var dispatched = (await dispatchResponse.Content.ReadFromJsonAsync<IncidentDispatchResponse>(Cancellation))!;

        await PostAsync<VisitExecutionResponse>(client, $"/api/visits/{dispatched.VisitId}/execution/start-work", null);
        var completed = await PostAsync<VisitExecutionResponse>(client, $"/api/visits/{dispatched.VisitId}/execution/complete", null);

        var incidentMessage = Assert.Single(await OutboxForAsync(incident.Id));
        Assert.Equal(("incident.dispatched", 1, "incident.dispatched"), (incidentMessage.Type, incidentMessage.Version, incidentMessage.RoutingKey));
        var incidentPayload = Payload(incidentMessage);
        Assert.Equal((incident.Id, dispatched.WorkOrderId, dispatched.VisitId, dispatched.Assignment.AssignmentId, technician),
            (incidentPayload.GetProperty("incidentId").GetGuid(), incidentPayload.GetProperty("workOrderId").GetGuid(), incidentPayload.GetProperty("visitId").GetGuid(),
             incidentPayload.GetProperty("assignmentId").GetGuid(), incidentPayload.GetProperty("technicianId").GetGuid()));
        Assert.Single(await OutboxForAsync(dispatched.Assignment.AssignmentId), m => m.Type == "assignment.created");

        // The visit's history has many operational events (created, status changes, work started...); only the
        // completion is published.
        var visitMessage = Assert.Single(await OutboxForAsync(dispatched.VisitId));
        Assert.Equal(("visit.work-completed", "visit.work.completed"), (visitMessage.Type, visitMessage.RoutingKey));
        var visitPayload = Payload(visitMessage);
        Assert.Equal((dispatched.VisitId, completed.ExecutionId!.Value, completed.Metrics.NetWorkMinutes!.Value),
            (visitPayload.GetProperty("visitId").GetGuid(), visitPayload.GetProperty("executionId").GetGuid(), visitPayload.GetProperty("netWorkMinutes").GetDecimal()));
        Assert.Empty(await OutboxForAsync(dispatched.WorkOrderId)); // WorkOrderCreated is internal history only
    }

    [Fact]
    public async Task The_request_correlation_id_reaches_the_operational_event_and_the_outbox_message()
    {
        using var client = factory.CreateClient();
        var technician = await CreateTechnicianAsync(client);
        var visit = await NewVisitAsync(client, NewOutboxDay().AddHours(9));
        var correlationId = Guid.NewGuid();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/visits/{visit.Id}/assignment")
        {
            Content = JsonContent.Create(new CreateAssignmentRequest(technician, null, null, null, null, null))
        };
        request.Headers.Add("X-Correlation-Id", correlationId.ToString());
        using var response = await client.SendAsync(request, Cancellation);
        var assignment = (await response.Content.ReadFromJsonAsync<AssignmentResponse>(Cancellation))!;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(correlationId.ToString(), response.Headers.GetValues("X-Correlation-Id").Single());
        var message = Assert.Single(await OutboxForAsync(assignment.AssignmentId));
        Assert.Equal(correlationId, message.CorrelationId);
        Assert.Equal(correlationId, Payload(message).GetProperty("correlationId").GetGuid());
        await using var scope = factory.Services.CreateAsyncScope();
        var operational = await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().OperationalEvents
            .SingleAsync(e => e.AggregateId == assignment.AssignmentId && e.EventType == "AssignmentCreated", Cancellation);
        Assert.Equal(correlationId, operational.CorrelationId);
    }

    [Fact]
    public async Task A_rolled_back_business_change_leaves_no_outbox_message()
    {
        using var client = factory.CreateClient();
        var technician = await CreateTechnicianAsync(client);
        var slot = NewOutboxDay().AddHours(9);
        var firstVisit = await NewVisitAsync(client, slot);
        var secondVisit = await NewVisitAsync(client, slot);

        // Two concurrent claims of one technician: the loser's assignment, reservations, operational event and outbox
        // message were all staged in its transaction, and all rolled back together.
        var responses = await Task.WhenAll(AssignAsync(client, firstVisit.Id, technician), AssignAsync(client, secondVisit.Id, technician));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        Assert.Equal(1, await OutboxMentioningAsync("assignment.created", "technicianId", technician));
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task A_failing_outbox_write_rolls_the_business_change_back()
    {
        using var client = factory.CreateClient();
        var technician = await CreateTechnicianAsync(client);
        var visit = await NewVisitAsync(client, NewOutboxDay().AddHours(9));

        await using (await FailOutboxInsertsAsync(technician))
        {
            using var response = await AssignAsync(client, visit.Id, technician);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        // Nothing of the assignment exists: the outbox failure aborted the whole transaction.
        using var active = await client.GetAsync($"/api/visits/{visit.Id}/assignment", Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, active.StatusCode);
        Assert.Equal(0, await ReservationCountAsync(factory, technician, Scheduling.Reservations.ResourceType.Technician));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CrewCallDbContext>();
        Assert.False(await db.OperationalEvents.AnyAsync(
            e => e.EventType == "AssignmentCreated" && EF.Functions.JsonContains(e.PayloadJson, $"{{\"technicianId\":\"{technician}\"}}"), Cancellation));
        Assert.Equal(0, await OutboxMentioningAsync("assignment.created", "technicianId", technician));

        // Once the outbox accepts the message again, the same request succeeds.
        using var retry = await AssignAsync(client, visit.Id, technician);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(1, await OutboxMentioningAsync("assignment.created", "technicianId", technician));
    }

    [Fact]
    public void Business_modules_and_the_api_never_reference_the_broker_client()
    {
        var assemblies = new[]
        {
            typeof(WorkOrders.WorkOrder).Assembly,
            typeof(Workforce.Technicians.Technician).Assembly,
            typeof(Resources.Vehicles.Vehicle).Assembly,
            typeof(Scheduling.Assignments.Assignment).Assembly,
            typeof(Scheduling.Core.TimeRange).Assembly,
            typeof(Contracts.Integration.IIntegrationEvent).Assembly,
            typeof(CrewCallDbContext).Assembly,
            typeof(Program).Assembly
        };

        foreach (var assembly in assemblies)
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name!.StartsWith("RabbitMQ", StringComparison.Ordinal));
        }

        // Nothing in the API host can even load the broker client: endpoints write business state and the outbox only.
        Assert.DoesNotContain(
            AppDomain.CurrentDomain.GetAssemblies().Where(a => a == typeof(Program).Assembly).SelectMany(a => a.GetReferencedAssemblies()),
            reference => reference.Name!.Contains("RabbitMQ", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location)!, "RabbitMQ.Client.dll")),
            "The API must not ship the RabbitMQ client.");
    }

    /// <summary>Makes the database reject outbox messages mentioning the technician until disposed.</summary>
    private async Task<IAsyncDisposable> FailOutboxInsertsAsync(Guid technicianId)
    {
        var name = $"test_fail_outbox_{technicianId:N}";
        await ExecuteAsync($"""
            CREATE FUNCTION ops.{name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'forced outbox failure'; END $$;
            CREATE TRIGGER {name} BEFORE INSERT ON ops.outbox_messages
            FOR EACH ROW WHEN (NEW.payload->>'technicianId' = '{technicianId}') EXECUTE FUNCTION ops.{name}();
            """);
        return new Cleanup(() => ExecuteAsync($"DROP TRIGGER {name} ON ops.outbox_messages; DROP FUNCTION ops.{name}();"));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
#pragma warning disable EF1002 // test-only DDL built from a Guid
        await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().Database.ExecuteSqlRawAsync(sql, Cancellation);
#pragma warning restore EF1002
    }

    private sealed class Cleanup(Func<Task> cleanup) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await cleanup();
    }
}
