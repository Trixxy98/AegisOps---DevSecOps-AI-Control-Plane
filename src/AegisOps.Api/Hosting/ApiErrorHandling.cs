namespace AegisOps.Api.Hosting;

/// <summary>
/// Unhandled exceptions become an RFC 9457 Problem Details 500 with a <c>traceId</c> extension and no exception detail
/// (roadmap v1.1 item 5, docs/17). The exception itself is logged by the exception handler middleware.
/// </summary>
public static class ApiErrorHandling {
    public static IServiceCollection AddApiProblemDetails(this IServiceCollection services) => services.AddProblemDetails();

    public static IApplicationBuilder UseApiExceptionHandler(this IApplicationBuilder app) => app.UseExceptionHandler();
}
