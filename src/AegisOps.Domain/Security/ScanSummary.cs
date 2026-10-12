namespace AegisOps.Domain.Security;

public sealed class ScanSummary {
    public int Critical {get; private set;}
    public int High {get; private set;}
    public int Medium {get; private set;}
    public int Low {get; private set;}
    public int Info {get; private set;}
    public int Unknown {get; private set;}
    public int Open {get; private set;}
    public int Suppressed {get; private set;}

    private ScanSummary() {
    }

    public static ScanSummary Empty() {
        return new ScanSummary();
    }

    public static ScanSummary From(IEnumerable<SecurityFinding> findings) {
        var summary = new ScanSummary();
        foreach (var finding in findings) {
            if (finding.Status == FindingStatus.Suppressed) {
                summary.Suppressed++;
                continue;
            }

            if (finding.Status != FindingStatus.Open) {
                continue;
            }

            summary.Open++;
            switch (finding.Severity) {
                case Severity.Critical:
                    summary.Critical++;
                    break;
                case Severity.High:
                    summary.High++;
                    break;
                case Severity.Medium:
                    summary.Medium++;
                    break;
                case Severity.Low:
                    summary.Low++;
                    break;
                case Severity.Info:
                    summary.Info++;
                    break;
                default:
                    summary.Unknown++;
                    break;
            }
        }

        return summary;
    }
}
