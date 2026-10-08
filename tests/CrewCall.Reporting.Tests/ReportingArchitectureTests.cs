extern alias reporting;

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.EntityFrameworkCore;
using reporting::CrewCall.Reporting.Data;
using Xunit;

namespace CrewCall.Reporting.Tests;

/// <summary>
/// CrewCall.Reporting is its own service boundary (ADR-0016): it depends only on the contracts, the shared broker
/// plumbing and the service defaults, never on the operational persistence or a business module, and it has no way to
/// address the operational database or the API.
/// </summary>
public sealed class ReportingArchitectureTests
{
    private static readonly Assembly Reporting = typeof(ReportingDbContext).Assembly;

    /// <summary>Every CrewCall assembly Reporting depends on, directly or through another CrewCall assembly.</summary>
    private static HashSet<string> CrewCallDependencies()
    {
        var seen = new HashSet<string>();
        var pending = new Queue<Assembly>([Reporting]);
        while (pending.TryDequeue(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies().Where(r => r.Name!.StartsWith("CrewCall.", StringComparison.Ordinal)))
            {
                if (seen.Add(reference.Name!))
                {
                    pending.Enqueue(Assembly.Load(reference));
                }
            }
        }

        return seen;
    }

    [Fact]
    public void Reporting_depends_only_on_contracts_messaging_and_service_defaults_never_on_operational_persistence_or_modules()
    {
        Assert.Equal(["CrewCall.Contracts", "CrewCall.Messaging", "CrewCall.ServiceDefaults"], CrewCallDependencies().Order());
    }

    [Fact]
    public void Reporting_has_no_operational_connection_string_api_address_or_operational_table_in_its_code()
    {
        var strings = UserStrings(Reporting.Location);

        Assert.Contains("crewcall-reporting", strings);
        Assert.DoesNotContain("crewcall", strings);       // the operational database's connection string name
        Assert.DoesNotContain(strings, s => s.Contains("crewcall-api", StringComparison.Ordinal));
        foreach (var operationalSchema in new[] { "workorders.", "workforce.", "resources.", "scheduling.", "ops." })
        {
            Assert.DoesNotContain(strings, s => s.Contains(operationalSchema, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Every_reporting_table_lives_in_the_reporting_schema_of_its_own_context()
    {
        using var db = new ReportingDbContext(new DbContextOptionsBuilder<ReportingDbContext>().UseNpgsql("Host=unused").Options);

        var tables = db.Model.GetEntityTypes().Select(entity => $"{entity.GetSchema()}.{entity.GetTableName()}").Order().ToList();

        Assert.Equal(
            ["reporting.assignment_activity", "reporting.inbox_messages", "reporting.incident_activity", "reporting.projection_checkpoints",
             "reporting.technician_activity", "reporting.visit_activity"],
            tables);
        Assert.Equal(typeof(DbContext), typeof(ReportingDbContext).BaseType);
    }

    private static List<string> UserStrings(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var strings = new List<string>();
        var handle = MetadataTokens.UserStringHandle(1);
        while (!handle.IsNil)
        {
            strings.Add(metadata.GetUserString(handle));
            handle = metadata.GetNextHandle(handle);
        }

        return strings;
    }
}
