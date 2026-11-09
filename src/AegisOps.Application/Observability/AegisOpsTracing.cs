using System.Diagnostics;

namespace AegisOps.Application.Observability;

public static class AegisOpsTracing {
    public const string SourceName = "AegisOps";
    public static readonly ActivitySource Source = new(SourceName);

    public static string SetCorrelation(Activity? activity, string? supplied) {
        var correlation = IsSafeCorrelation(supplied) ? supplied! : Guid.CreateVersion7().ToString("N");
        if (activity is not null) {
            activity.SetTag("correlationId", correlation);
            activity.SetBaggage("correlationId", correlation);
        }
        return correlation;
    }

    public static bool IsSafeCorrelation(string? value) => value is { Length: > 0 and <= 64 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
