using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Creates the <c>data_protection_keys</c> table for the Authenticated Foundation
/// (Phase 1 step 4 of <c>docs/proposals/security-and-authorization.md</c> §9.3). The
/// table backs ASP.NET Core Data Protection key persistence via the
/// <c>AspNetCore.DataProtection.CustomStorage.Dapper.PostgreSQL</c> package so that
/// antiforgery / authentication cookie keys are shared across replicas on Render
/// (otherwise every cold-started instance would mint its own ring and invalidate
/// every other instance's cookies). The package can auto-create this table via its
/// <c>InitializeTable</c> option, but Heimdall owns its schema in FluentMigrator —
/// startup-time DDL is disabled in <c>Program.cs</c> (<c>InitializeTable = false</c>)
/// so deployments fail loudly on a missing migration rather than silently creating
/// drift between environments. Schema, column types, identity column, PK constraint
/// name, and the unique index on <c>friendly_name</c> all match what the package's
/// <c>InitializeDb()</c> would otherwise emit (see
/// <c>PostgreSQLDataProtectionRepository.InitializeDb</c>) so the package's queries
/// resolve identically.
/// </summary>
[Migration(202605050003, "Create data_protection_keys")]
public class M202605050003_CreateDataProtectionKeys : Migration
{
    /// <summary>
    /// Creates the <c>data_protection_keys</c> table and its supporting unique index.
    /// Authored in raw SQL — matching <c>M202605050001_CreateUsers</c> and
    /// <c>M202605050002_CreateAuditEvents</c> — because FluentMigrator's fluent column
    /// API does not natively express PostgreSQL's <c>GENERATED ALWAYS AS IDENTITY</c>
    /// nor the <c>COLLATE pg_catalog."default"</c> qualifier the package's repository
    /// emits, and the index needs <c>ASC NULLS LAST</c> ordering to match the package
    /// exactly.
    /// </summary>
    public override void Up()
    {
        Execute.Sql(@"
CREATE TABLE data_protection_keys (
    id            integer                       GENERATED ALWAYS AS IDENTITY,
    insert_date   timestamp with time zone      NOT NULL DEFAULT now(),
    friendly_name character varying(256)        COLLATE pg_catalog.""default"" NULL,
    xml           text                          COLLATE pg_catalog.""default"" NOT NULL,
    CONSTRAINT pk_public_data_protection_keys PRIMARY KEY (id)
);");

        // Unique index on friendly_name. The package's GetAll() does not require it,
        // but Insert() relies on the application not double-writing the same key, and
        // the package's auto-init creates this index — keeping it here means the
        // managed schema is byte-for-byte equivalent to the auto-init path.
        Execute.Sql(@"
CREATE UNIQUE INDEX ix_public_data_protection_keys_friendly_name
    ON data_protection_keys USING btree
    (friendly_name COLLATE pg_catalog.""default"" ASC NULLS LAST);");
    }

    /// <summary>
    /// Drops the <c>data_protection_keys</c> table. The unique index is dropped
    /// implicitly with the table.
    /// </summary>
    public override void Down()
    {
        Delete.Table("data_protection_keys");
    }
}
