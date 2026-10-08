using CrewCall.Persistence.Messaging;

namespace CrewCall.Api;

/// <summary>
/// Takes the request's correlation id from the X-Correlation-Id header (a GUID) and hands it to the request scope, so
/// operational events and outbox messages written by the request carry it (ADR-0014). The id is echoed back. Requests
/// without one stay uncorrelated: this is not a full distributed tracing model.
/// </summary>
internal static class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";

    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (Guid.TryParse(context.Request.Headers[HeaderName], out var correlationId) && correlationId != Guid.Empty)
            {
                context.RequestServices.GetRequiredService<CorrelationContext>().CorrelationId = correlationId;
                context.Response.Headers[HeaderName] = correlationId.ToString();
            }

            await next(context);
        });
}
