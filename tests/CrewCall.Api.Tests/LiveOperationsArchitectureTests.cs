using System.Reflection;
using CrewCall.Persistence;
using Xunit;

namespace CrewCall.Api.Tests;

/// <summary>
/// SignalR is a delivery channel (ADR-0015): only the delivery layer (CrewCall.Integrations hosts the hub, CrewCall.Web is
/// a client) may know it. Business modules, the persistence layer, the contracts and the API never reference it, so no
/// business code can call IHubContext.
/// </summary>
public sealed class LiveOperationsArchitectureTests
{
    private static readonly Assembly[] DomainAndApi =
    [
        typeof(WorkOrders.WorkOrder).Assembly,
        typeof(Workforce.Technicians.Technician).Assembly,
        typeof(Resources.Vehicles.Vehicle).Assembly,
        typeof(Scheduling.Assignments.Assignment).Assembly,
        typeof(Scheduling.Core.TimeRange).Assembly,
        typeof(Contracts.Live.LiveOperationMessage).Assembly,
        typeof(CrewCallDbContext).Assembly,
        typeof(Program).Assembly
    ];

    [Fact]
    public void Business_modules_persistence_contracts_and_the_api_do_not_reference_SignalR()
    {
        foreach (var assembly in DomainAndApi)
        {
            Assert.DoesNotContain(
                assembly.GetReferencedAssemblies(),
                reference => reference.Name!.StartsWith("Microsoft.AspNetCore.SignalR", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void No_business_type_depends_on_a_SignalR_type_such_as_IHubContext()
    {
        static bool IsSignalR(Type type) =>
            type.Namespace?.StartsWith("Microsoft.AspNetCore.SignalR", StringComparison.Ordinal) == true
            || (type.IsGenericType && type.GetGenericArguments().Any(IsSignalR));

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var offenders =
            from assembly in DomainAndApi
            from type in assembly.GetTypes()
            let dependencies = type.GetConstructors(all).SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType)
                .Concat(type.GetFields(all).Select(field => field.FieldType))
                .Concat(type.GetProperties(all).Select(property => property.PropertyType))
                .Append(type.BaseType ?? typeof(object))
            where dependencies.Any(IsSignalR)
            select type.FullName;

        Assert.Empty(offenders);
    }
}
