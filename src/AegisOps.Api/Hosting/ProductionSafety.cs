using System.Data.Common;
using AegisOps.Infrastructure.Identity;

namespace AegisOps.Api.Hosting;

/// <summary>
/// Startup refusal of obviously unsafe configuration outside Development (roadmap v1.1 item 1, docs/14).
/// Missing <c>ConnectionStrings:Postgres</c> and a JWT key under 32 bytes are already refused in every environment by
/// <c>AddIdentityStore</c> and <c>AddJwtAuthentication</c>; this adds the checks that only make sense for a real
/// installation. Messages name the setting, never its value.
/// </summary>
public static class ProductionSafety {
    /// <summary>The placeholder in <c>.env.example</c>; copying the example file unchanged must not start in Production.</summary>
    public const string ExampleSigningKey = "replace-this-with-a-random-secret-of-at-least-32-bytes";

    /// <summary>The Development database password of docker-compose.yml and appsettings.Development.json.</summary>
    public const string DevelopmentDatabasePassword = "aegisops_dev";

    public static IReadOnlyList<string> Problems(IConfiguration configuration, IHostEnvironment environment) {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.IsDevelopment()) {
            return [];
        }

        var problems = new List<string>();
        var signingKey = configuration[$"{JwtOptions.SectionName}:SigningKey"]?.Trim();
        if (string.IsNullOrEmpty(signingKey)) {
            problems.Add("Jwt:SigningKey (JWT_SIGNING_KEY) is missing.");
        } else if (string.Equals(signingKey, ExampleSigningKey, StringComparison.Ordinal)) {
            problems.Add("Jwt:SigningKey (JWT_SIGNING_KEY) is still the .env.example placeholder; run scripts/gen-secrets.sh.");
        }

        var postgres = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(postgres)) {
            problems.Add("ConnectionStrings:Postgres is missing.");
        } else if (UsesDevelopmentPassword(postgres)) {
            problems.Add("ConnectionStrings:Postgres uses the Development password; set POSTGRES_PASSWORD (scripts/gen-secrets.sh).");
        }

        return problems;
    }

    public static void ThrowIfUnsafe(IConfiguration configuration, IHostEnvironment environment) {
        var problems = Problems(configuration, environment);
        if (problems.Count > 0) {
            throw new InvalidOperationException(
                $"Refusing to start in the {environment.EnvironmentName} environment: " + string.Join(" ", problems));
        }
    }

    private static bool UsesDevelopmentPassword(string connectionString) {
        try {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            return builder.TryGetValue("Password", out var password) &&
                string.Equals(password?.ToString(), DevelopmentDatabasePassword, StringComparison.Ordinal);
        } catch (ArgumentException) {
            return false; // Malformed strings are reported by Npgsql itself on first use.
        }
    }
}
