using CrewCall.Web.Components;
using CrewCall.Web.Operations;
using CrewCall.Web.Status;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Interactive server rendering for the manager screen (/operations) and the developer page /live: each circuit holds a
// SignalR client to the live operations hub. The status page stays static.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// EXTEXP0001: RemoveAllResilienceHandlers is marked experimental, but it is the documented way to opt one
// client out of the default resilience pipeline. A retried 503 from /health would hide the real state.
#pragma warning disable EXTEXP0001
builder.Services.AddHttpClient<SystemStatusClient>(client =>
    {
        // Logical service name resolved by Aspire service discovery; HTTPS preferred, HTTP allowed.
        client.BaseAddress = new("https+http://crewcall-api");
        client.Timeout = TimeSpan.FromSeconds(5);
    })
    // A status probe must report the current state, so it does not retry.
    .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

// The manager screen talks to the API and, for the day's numbers, to the reporting service (both by service discovery).
builder.Services.AddHttpClient<OperationsApiClient>(client => client.BaseAddress = new("https+http://crewcall-api"));
builder.Services.AddHttpClient<ReportingApiClient>(client =>
{
    client.BaseAddress = new("https+http://crewcall-reporting-service");
    client.Timeout = TimeSpan.FromSeconds(5);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
