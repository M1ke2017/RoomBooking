using Daylo.Web;
using Daylo.Web.Components;
using Daylo.Web.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// "ConnectionStrings:daylo" comes from Aspire when started through Daylo.AppHost, or from configuration otherwise.
builder.Services.AddDbContext<DayloDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("daylo")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");
builder.Services.AddRazorComponents();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // Development only: bring the database up to date on startup. A shared environment runs migrations as a deploy step.
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<DayloDbContext>();
    await db.Database.MigrateAsync();

    if (app.Configuration.GetValue<bool>("Demo:Seed"))
    {
        await DemoData.SeedAsync(db, app.Services.GetRequiredService<TimeProvider>().GetUtcNow(), CancellationToken.None);
    }
}
else
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Redirect("/today"));
app.MapRazorComponents<App>();

app.Run();

public partial class Program;
