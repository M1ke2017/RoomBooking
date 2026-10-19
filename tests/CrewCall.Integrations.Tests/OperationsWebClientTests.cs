extern alias web;

using System.Net;
using System.Text;
using CrewCall.Contracts.Incidents;
using web::CrewCall.Web.Operations;
using Xunit;

namespace CrewCall.Integrations.Tests;

/// <summary>
/// The manager screen's API client (CrewCall.Web, Sprint 16): what the screen shows when the API refuses, e.g. a stale
/// proposal (409), and when the API is down: the manager's words, never the API's raw detail or an exception. The page itself is checked in a browser (see the Sprint 16 report).
/// </summary>
public sealed class OperationsWebClientTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static readonly RescheduleProposal Proposal = new(
        1, Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), Guid.NewGuid(), "Jan Kowalski", null, [], 0, 30,
        Guid.NewGuid(), Guid.NewGuid(), "Normal", DateTimeOffset.UtcNow.AddHours(2), DateTimeOffset.UtcNow.AddHours(3),
        new RescheduleImpact(1, 1, 120, true, [], []), true);

    [Fact]
    public async Task A_stale_proposal_is_explained_to_the_manager_and_the_exact_proposal_is_what_gets_accepted()
    {
        var handler = new StubHandler(HttpStatusCode.Conflict,
            """{"title":"Proposal out of date","detail":"Visit 'x' is InProgress; only planned visits are moved. Request a new proposal.","status":409}""");
        var client = new OperationsApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://api") });

        var result = await client.AcceptAsync(Proposal.IncidentId, Proposal, Cancellation);

        Assert.False(result.Succeeded);
        Assert.Equal("Plan zmienił się od przygotowania propozycji. Pokaż nową propozycję.", result.Error);
        Assert.Equal($"/api/incidents/{Proposal.IncidentId}/reschedule-proposal/apply", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains($"\"movedVisitId\":\"{Proposal.ConflictingVisitId}\"", handler.LastBody);
        Assert.Contains("\"travelBufferAfterMinutes\":30", handler.LastBody);
    }

    [Fact]
    public async Task A_rejected_request_and_an_unreachable_API_are_said_plainly()
    {
        var invalid = new OperationsApiClient(new HttpClient(new StubHandler(HttpStatusCode.BadRequest,
            """{"title":"One or more validation errors occurred.","status":400,"errors":{"date":["Must be a date, yyyy-MM-dd."]}}"""))
        {
            BaseAddress = new Uri("http://api")
        });
        var down = new OperationsApiClient(new HttpClient(new StubHandler(new HttpRequestException("Connection refused"))) { BaseAddress = new Uri("http://api") });

        var rejected = await invalid.GetBoardAsync(new DateOnly(2038, 6, 7), Cancellation);
        var unreachable = await down.GetBoardAsync(null, Cancellation);

        Assert.Equal("Serwer odrzucił żądanie jako nieprawidłowe. Odśwież widok i spróbuj ponownie.", rejected.Error);
        Assert.Null(rejected.Value);
        Assert.Equal(OperationsApiClient.Unreachable, unreachable.Error);
        Assert.DoesNotContain("Connection refused", unreachable.Error);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body = "";
        private readonly Exception? _failure;

        public StubHandler(HttpStatusCode status, string body) => (_status, _body) = (status, body);

        public StubHandler(Exception failure) => _failure = failure;

        public HttpRequestMessage? LastRequest { get; private set; }

        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (_failure is not null)
            {
                throw _failure;
            }

            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/problem+json") };
        }
    }
}
