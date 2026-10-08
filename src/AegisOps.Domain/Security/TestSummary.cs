namespace AegisOps.Domain.Security;

public sealed class TestSummary {
    public int Total {get; private set;}
    public int Passed {get; private set;}
    public int Failed {get; private set;}
    public int Skipped {get; private set;}
    public double DurationSeconds {get; private set;}

    private TestSummary() {
    }

    public static TestSummary Create(
        int total,
        int passed,
        int failed,
        int skipped,
        double durationSeconds
    ) {
        if (total < 0 || passed < 0 || failed < 0 || skipped < 0) {
            throw new ArgumentException("Test counts must not be negative.");
        }

        if (passed + failed + skipped != total) {
            throw new ArgumentException("Passed, failed, and skipped must add up to total.");
        }

        if (durationSeconds < 0 || double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds)) {
            throw new ArgumentException("Duration must be a non-negative number.", nameof(durationSeconds));
        }

        return new TestSummary {
            Total = total,
            Passed = passed,
            Failed = failed,
            Skipped = skipped,
            DurationSeconds = durationSeconds,
        };
    }
}