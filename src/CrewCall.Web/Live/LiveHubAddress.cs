using CrewCall.Contracts.Live;

namespace CrewCall.Web.Live;

/// <summary>
/// Where the live operations hub is. No URL is hard-coded: an explicit "LiveOperations:HubUrl" wins; otherwise the
/// endpoint Aspire service discovery provides for crewcall-integrations (HTTPS preferred) plus the hub path.
/// </summary>
public static class LiveHubAddress
{
    public const string ServiceName = "crewcall-integrations";

    public static Uri? Resolve(IConfiguration configuration)
    {
        if (configuration["LiveOperations:HubUrl"] is { Length: > 0 } explicitUrl)
        {
            return new Uri(explicitUrl, UriKind.Absolute);
        }

        var serviceBase = configuration[$"services:{ServiceName}:https:0"] ?? configuration[$"services:{ServiceName}:http:0"];
        return serviceBase is { Length: > 0 } ? new Uri(new Uri(serviceBase, UriKind.Absolute), LiveOperationsHubContract.Path) : null;
    }
}
