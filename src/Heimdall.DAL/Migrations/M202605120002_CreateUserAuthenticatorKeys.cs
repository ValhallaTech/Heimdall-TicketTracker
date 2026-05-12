using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Creates the <c>user_authenticator_keys</c> table (Phase 4.1 step 2 of
/// <c>docs/proposals/security-and-authorization.md</c> §9.3). One row per user holds the
/// base32 TOTP shared secret used by an authenticator app. The secret is stored verbatim
/// because TOTP verification needs the original key for HMAC-SHA1 — hashing would break
/// the protocol. The decision-log entry of 2026-05-05 explicitly rejected re-introducing
/// <c>IDataProtector</c>; confidentiality at rest is handled by Postgres TDE / volume
/// encryption plus the runbook controls deferred to Phase 4.7 step 23.
/// </summary>
[Migration(202605120002, "Create user_authenticator_keys")]
public class M202605120002_CreateUserAuthenticatorKeys : Migration
{
    /// <summary>
    /// Creates the <c>user_authenticator_keys</c> table with <c>user_id</c> as the primary
    /// key (one authenticator per user), a CASCADE foreign key to <c>users(id)</c>, the
    /// provider discriminator (<c>"Authenticator"</c>), the base32 shared secret, and a
    /// <c>created_at</c> audit timestamp defaulted to <c>now()</c>.
    /// </summary>
    public override void Up()
    {
        Execute.Sql(@"
CREATE TABLE user_authenticator_keys (
    user_id            uuid        NOT NULL PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    provider_name      text        NOT NULL,
    authenticator_key  text        NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now()
);");
    }

    /// <summary>
    /// Drops the <c>user_authenticator_keys</c> table.
    /// </summary>
    public override void Down()
    {
        Delete.Table("user_authenticator_keys");
    }
}
