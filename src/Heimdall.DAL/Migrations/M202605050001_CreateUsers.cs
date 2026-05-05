using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Creates the <c>users</c> table for the Authenticated Foundation (Phase 1 step 1 of
/// <c>docs/proposals/security-and-authorization.md</c> §9.3). The schema mirrors the columns
/// required by ASP.NET Core Identity's user store (email + normalized email, password hash,
/// security / concurrency stamps, lockout fields) plus a <c>system_admin</c> flag and the
/// standard <c>created_at</c> / <c>updated_at</c> audit columns. RBAC / PBAC tables (roles,
/// permissions, groups, joins) are intentionally excluded — they were dropped in PR #25.
/// </summary>
[Migration(202605050001, "Create users")]
public class M202605050001_CreateUsers : Migration
{
    /// <summary>
    /// Creates the <c>users</c> table along with the <c>pgcrypto</c> and <c>citext</c>
    /// extensions it depends on. <c>pgcrypto</c> supplies <c>gen_random_uuid()</c> for the
    /// primary key default; <c>citext</c> gives us case-insensitive email columns without
    /// a functional index. Uniqueness for <c>email</c> and <c>normalized_email</c> is
    /// enforced via dedicated unique indexes (named so pgTAP can assert against them).
    /// </summary>
    public override void Up()
    {
        // Required for gen_random_uuid() and the citext type. IF NOT EXISTS keeps this
        // migration idempotent across environments where ops may have pre-installed them.
        Execute.Sql("CREATE EXTENSION IF NOT EXISTS pgcrypto;");
        Execute.Sql("CREATE EXTENSION IF NOT EXISTS citext;");

        // FluentMigrator's fluent column API does not natively understand citext or the
        // gen_random_uuid() default expression, and mixing fluent + raw column-level SQL
        // is awkward. Following the precedent set by M202604300001_CreateTickets (which
        // uses Execute.Sql for CHECK constraints and ordered indexes), we author the full
        // CREATE TABLE in raw SQL for clarity and to keep all the column types correct.
        Execute.Sql(@"
CREATE TABLE users (
    id                   uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    email                citext      NOT NULL,
    normalized_email     citext      NOT NULL,
    password_hash        text        NULL,
    security_stamp       text        NOT NULL,
    concurrency_stamp    text        NOT NULL,
    email_confirmed      boolean     NOT NULL DEFAULT false,
    lockout_end          timestamptz NULL,
    lockout_enabled      boolean     NOT NULL DEFAULT true,
    access_failed_count  integer     NOT NULL DEFAULT 0,
    system_admin         boolean     NOT NULL DEFAULT false,
    created_at           timestamptz NOT NULL,
    updated_at           timestamptz NOT NULL
);");

        // Dedicated, named unique indexes (rather than inline UNIQUE constraints) so pgTAP
        // can assert against stable index names and so we can drop / recreate them
        // independently in future migrations if the lookup pattern changes.
        Execute.Sql("CREATE UNIQUE INDEX ux_users_email ON users (email);");
        Execute.Sql(
            "CREATE UNIQUE INDEX ux_users_normalized_email ON users (normalized_email);"
        );
    }

    /// <summary>
    /// Drops the <c>users</c> table. The <c>pgcrypto</c> and <c>citext</c> extensions are
    /// intentionally left in place — they may be used by other objects, and dropping
    /// extensions in a down migration is rarely the right call.
    /// </summary>
    public override void Down()
    {
        Delete.Table("users");
    }
}
