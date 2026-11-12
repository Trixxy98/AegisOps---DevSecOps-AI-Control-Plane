using AegisOps.Api.Identity;
using AegisOps.Application.Audit;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Identity;
using AegisOps.Infrastructure.Observability;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Hosting;

/// <summary>
/// One-shot operator commands of the API image (roadmap v1.1 items 1–2), run instead of the web host:
/// <code>
/// dotnet AegisOps.Api.dll migrate
/// dotnet AegisOps.Api.dll bootstrap-admin --email you@example.com [--display-name "Your Name"]
/// </code>
/// Both work in every environment and only need <c>ConnectionStrings:Postgres</c>. Exit codes: 0 success,
/// 1 refused or failed, 2 usage error.
/// </summary>
public static class AdminCommands {
    public const string Migrate = "migrate";
    public const string BootstrapAdminCommand = "bootstrap-admin";
    public const int UsageError = 2;

    private const string Usage = """
        Usage:
          dotnet AegisOps.Api.dll migrate
              Apply database migrations, create the Admin, Developer, Security and Approver roles and, when
              AppDatabase:Password (APP_DB_PASSWORD) is set, the restricted aegisops_app database role.
          dotnet AegisOps.Api.dll bootstrap-admin --email <address> [--display-name <name>]
              Create the first Admin with a temporary password (refused when an Admin exists).
        """;

    public static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && (args[0] is Migrate or BootstrapAdminCommand or "help" or "--help");

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error) {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args[0] is "help" or "--help") {
            await output.WriteLineAsync(Usage);
            return 0;
        }

        string? email = null;
        string? displayName = null;
        if (args[0] == BootstrapAdminCommand) {
            if (!TryParseBootstrapOptions(args.AsSpan(1), out email, out displayName, out var problem)) {
                await error.WriteLineAsync($"bootstrap-admin: {problem}");
                await error.WriteLineAsync(Usage);
                return UsageError;
            }
        } else if (args.Length > 1) {
            await error.WriteLineAsync($"{args[0]}: unexpected argument '{args[1]}'.");
            await error.WriteLineAsync(Usage);
            return UsageError;
        }

        await using var services = BuildServices();
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => {
            eventArgs.Cancel = true;
            stop.Cancel();
        };

        return args[0] == Migrate
            ? await MigrateAsync(services, output, static (db, token) => db.Database.MigrateAsync(token), stop.Token)
            : await BootstrapAdmin.RunAsync(services, email!, displayName, output, error, stop.Token);
    }

    /// <summary>
    /// Applies migrations (<paramref name="applyMigrations"/>; tests substitute <c>EnsureCreated</c> on SQLite) and then
    /// ensures the four roles. Idempotent.
    /// </summary>
    public static async Task<int> MigrateAsync(
        IServiceProvider services,
        TextWriter output,
        Func<AegisOpsDbContext, CancellationToken, Task> applyMigrations,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(applyMigrations);
        await using var scope = services.CreateAsyncScope();
        await applyMigrations(scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>(), cancellationToken);
        await output.WriteLineAsync("Database migrations are applied.");
        var created = await IdentityRoles.EnsureAsync(scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>());
        await output.WriteLineAsync(created.Count == 0
            ? "Roles already present: " + string.Join(", ", IdentityRoles.All) + "."
            : "Created roles: " + string.Join(", ", created) + ".");
        await EnsureAppDatabaseRoleAsync(scope.ServiceProvider, output, cancellationToken);
        return 0;
    }

    /// <summary>
    /// Roadmap v1.1 item 6: when <c>AppDatabase:Password</c> (<c>APP_DB_PASSWORD</c>) is configured, creates or updates the
    /// restricted <see cref="AppDatabaseRole.DefaultName"/> role used by the production api and worker (SELECT/INSERT only on
    /// <c>audit</c>). Without it, nothing changes: the Development stack keeps connecting as the owner. Never prints the
    /// password.
    /// </summary>
    private static async Task EnsureAppDatabaseRoleAsync(IServiceProvider services, TextWriter output, CancellationToken cancellationToken) {
        var password = services.GetService<IConfiguration>()?[AppDatabaseRole.PasswordKey];
        if (string.IsNullOrWhiteSpace(password)) {
            await output.WriteLineAsync(
                $"{AppDatabaseRole.PasswordKey} (APP_DB_PASSWORD) is not set: the restricted role {AppDatabaseRole.DefaultName} was not created or updated.");
            return;
        }

        var db = services.GetRequiredService<AegisOpsDbContext>();
        if (!db.Database.IsNpgsql()) {
            await output.WriteLineAsync("Not a PostgreSQL database: the application role was skipped.");
            return;
        }

        await AppDatabaseRole.EnsureAsync(db, AppDatabaseRole.DefaultName, password, cancellationToken);
        await output.WriteLineAsync(
            $"Database role {AppDatabaseRole.DefaultName} is up to date (audit: SELECT and INSERT only).");
    }

    public static bool TryParseBootstrapOptions(
        ReadOnlySpan<string> options,
        out string? email,
        out string? displayName,
        out string problem
    ) {
        email = null;
        displayName = null;
        problem = string.Empty;
        for (var index = 0; index < options.Length; index++) {
            var option = options[index];
            string name;
            string? value;
            var equals = option.IndexOf('=', StringComparison.Ordinal);
            if (option.StartsWith("--", StringComparison.Ordinal) && equals > 0) {
                name = option[..equals];
                value = option[(equals + 1)..];
            } else {
                name = option;
                value = index + 1 < options.Length ? options[++index] : null;
            }

            if (value is null) {
                problem = $"{name} needs a value.";
                return false;
            }

            switch (name) {
                case "--email":
                    email = value;
                    break;
                case "--display-name":
                    displayName = value;
                    break;
                default:
                    problem = $"unknown option '{name}'.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(email)) {
            problem = "--email is required.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// The same configuration sources as the web host (appsettings, environment variables, user secrets in Development),
    /// but only the database, Identity and audit services. Command arguments are not configuration.
    /// </summary>
    private static ServiceProvider BuildServices() {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(builder.Configuration);
        services.AddLogging();
        services.AddAegisOpsSerilog(builder.Configuration, builder.Environment,
            new AegisOpsLoggingOptions(AegisOpsLogging.ApiServiceName));
        services.AddIdentityStore(builder.Configuration);
        services.AddScoped<IAuditWriter, EfAuditWriter>();
        return services.BuildServiceProvider(validateScopes: true);
    }
}
