using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CrewCall.Reporting.Data;

/// <summary>
/// Used only by the EF Core tools (dotnet ef migrations ...). Adding a migration does not open a connection, so the
/// placeholder below is never used to connect.
/// </summary>
internal sealed class DesignTimeReportingDbContextFactory : IDesignTimeDbContextFactory<ReportingDbContext>
{
    private const string PlaceholderConnectionString = "Host=design-time-placeholder;Database=crewcall_reporting";

    public ReportingDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ReportingDbContext>();
        ReportingDbContext.Configure(builder, Environment.GetEnvironmentVariable("CREWCALL_REPORTING_DESIGN_TIME_CONNECTION") ?? PlaceholderConnectionString);
        return new ReportingDbContext(builder.Options);
    }
}
