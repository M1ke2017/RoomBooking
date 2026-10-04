using CrewCall.Web.Components;
using CrewCall.Web.Status;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorComponents();

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

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>();

app.MapDefaultEndpoints();

app.Run();
