using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Infrastructure.Persistence;

/// <summary>
/// Audit tamper protection (roadmap v1.1 item 6, docs/17 §2): provisions the restricted LOGIN role the API and Worker
/// use in production. Run by the <c>migrate</c> command as the owner role (the one that ran the migrations), it
/// <list type="bullet">
/// <item>creates the role or updates its password (idempotent) and refuses a role with superuser, CREATEROLE,
/// CREATEDB, replication or BYPASSRLS attributes, or one that is a member of the owner role;</item>
/// <item>grants USAGE and SELECT/INSERT/UPDATE/DELETE (sequences: USAGE/SELECT/UPDATE) on every schema owned by the
/// owner except <c>audit</c>, plus <c>ALTER DEFAULT PRIVILEGES</c> so tables created later get the same grants;</item>
/// <item>on <c>audit</c> resets the grants to SELECT and INSERT only: no UPDATE, DELETE, TRUNCATE, REFERENCES or
/// TRIGGER, now and for future tables;</item>
/// <item>leaves the EF migrations history table read-only (the readiness check reads it).</item>
/// </list>
/// The role name and password reach the server as bind parameters (<c>set_config</c>, transaction-local) and are
/// quoted with <c>format('%I'/'%L')</c> inside the DO block; a failure while setting the password is re-raised
/// without the statement text, so the password is never echoed in an error or logged by this code.
/// </summary>
public static partial class AppDatabaseRole {
    /// <summary>The role of the production api and worker connection strings (deploy/docker-compose.prod.yml).</summary>
    public const string DefaultName = "aegisops_app";

    public const string AuditSchema = "audit";

    /// <summary>Configuration key of the role's password (<c>APP_DB_PASSWORD</c> in <c>.env</c>).</summary>
    public const string PasswordKey = "AppDatabase:Password";


    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RoleNamePattern();

    public static bool IsValidRoleName(string? roleName) => roleName is not null && RoleNamePattern().IsMatch(roleName);

    /// <summary>
    /// Creates or updates <paramref name="roleName"/> with <paramref name="password"/> and applies the grants described
    /// on the class, in one transaction on <paramref name="db"/>'s connection (which must be the owner). PostgreSQL only.
    /// </summary>
    public static async Task EnsureAsync(AegisOpsDbContext db, string roleName, string password, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsValidRoleName(roleName)) {
            throw new ArgumentException("The role name must be a lower-case PostgreSQL identifier.", nameof(roleName));
        }

        if (string.IsNullOrWhiteSpace(password) || password.Length < 16) {
            throw new ArgumentException("The application role password must be at least 16 characters.", nameof(password));
        }

        if (!db.Database.IsNpgsql()) {
            throw new InvalidOperationException("The application role can only be provisioned on PostgreSQL.");
        }

        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var settings = connection.CreateCommand()) {
                settings.Transaction = transaction;
                settings.CommandText =
                    "SELECT set_config('aegisops.app_role', @role, true), set_config('aegisops.app_password', @password, true)";
                AddParameter(settings, "role", roleName);
                AddParameter(settings, "password", password);
                await settings.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var provision = connection.CreateCommand()) {
                provision.Transaction = transaction;
                // A compile-time literal: no input reaches this text (the role and password arrive via set_config above).
                provision.CommandText = """
                    DO $provision$
                    DECLARE
                        app_role text := current_setting('aegisops.app_role');
                        app_password text := current_setting('aegisops.app_password');
                        owner_role text := current_user;
                        item record;
                    BEGIN
                        IF app_role = owner_role THEN
                            RAISE EXCEPTION 'The application role % must differ from the owner role.', app_role;
                        END IF;

                        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = app_role) THEN
                            EXECUTE format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION', app_role);
                        END IF;

                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = app_role
                                   AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolreplication OR rolbypassrls)) THEN
                            RAISE EXCEPTION 'Role % has superuser, CREATEROLE, CREATEDB, REPLICATION or BYPASSRLS; refusing to use it as the application role.', app_role;
                        END IF;
                        IF pg_has_role(app_role, owner_role, 'MEMBER') THEN
                            RAISE EXCEPTION 'Role % is a member of the owner role %; refusing to use it as the application role.', app_role, owner_role;
                        END IF;

                        BEGIN
                            EXECUTE format('ALTER ROLE %I WITH LOGIN PASSWORD %L', app_role, app_password);
                        EXCEPTION WHEN OTHERS THEN
                            -- Never surface the statement (it contains the password).
                            RAISE EXCEPTION 'Could not set the password of role % (SQLSTATE %).', app_role, SQLSTATE;
                        END;

                        EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), app_role);

                        FOR item IN
                            SELECT nspname FROM pg_namespace
                            WHERE nspowner = (SELECT oid FROM pg_roles WHERE rolname = owner_role)
                              AND nspname NOT IN ('public', 'information_schema', 'audit')
                              AND nspname NOT LIKE 'pg\_%'
                        LOOP
                            EXECUTE format('GRANT USAGE ON SCHEMA %I TO %I', item.nspname, app_role);
                            EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO %I', item.nspname, app_role);
                            EXECUTE format('GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA %I TO %I', item.nspname, app_role);
                            EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I', item.nspname, app_role);
                            EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO %I', item.nspname, app_role);
                        END LOOP;

                        IF EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'audit') THEN
                            EXECUTE format('GRANT USAGE ON SCHEMA audit TO %I', app_role);
                            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA audit FROM %I', app_role);
                            EXECUTE format('GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA audit TO %I', app_role);
                            EXECUTE format('REVOKE ALL ON ALL SEQUENCES IN SCHEMA audit FROM %I', app_role);
                            EXECUTE format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA audit TO %I', app_role);
                            EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA audit REVOKE ALL ON TABLES FROM %I', app_role);
                            EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT SELECT, INSERT ON TABLES TO %I', app_role);
                            EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA audit REVOKE ALL ON SEQUENCES FROM %I', app_role);
                            EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT USAGE, SELECT ON SEQUENCES TO %I', app_role);
                            REVOKE UPDATE, DELETE, TRUNCATE ON ALL TABLES IN SCHEMA audit FROM PUBLIC;
                        END IF;

                        FOR item IN SELECT schemaname, tablename FROM pg_tables WHERE tablename = '__EFMigrationsHistory' LOOP
                            EXECUTE format('GRANT USAGE ON SCHEMA %I TO %I', item.schemaname, app_role);
                            EXECUTE format('REVOKE ALL ON TABLE %I.%I FROM %I', item.schemaname, item.tablename, app_role);
                            EXECUTE format('GRANT SELECT ON TABLE %I.%I TO %I', item.schemaname, item.tablename, app_role);
                        END LOOP;
                    END
                    $provision$;
                    """;
                await provision.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        } finally {
            if (opened) await connection.CloseAsync();
        }
    }

    private static void AddParameter(DbCommand command, string name, string value) {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
