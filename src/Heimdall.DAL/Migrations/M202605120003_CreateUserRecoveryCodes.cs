using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Creates the <c>user_recovery_codes</c> table (Phase 4.1 step 3 of
/// <c>docs/proposals/security-and-authorization.md</c> §9.3). Recovery codes are stored
/// as hashes produced by the Identity <c>IPasswordHasher&lt;HeimdallUser&gt;</c> — never
/// plaintext and never reversibly encrypted — and are redeemed by verifying the supplied
/// code against each unused hash. A composite index on <c>(user_id, used_at)</c> supports
/// the "any unused codes left?" lookup. No unique constraint exists on <c>code_hash</c>:
/// codes are server-generated and globally random, and enforcing uniqueness on the hash
/// would require equality comparisons over values that must never be equal-checked.
/// </summary>
[Migration(202605120003, "Create user_recovery_codes")]
public class M202605120003_CreateUserRecoveryCodes : Migration
{
    /// <summary>
    /// Creates the <c>user_recovery_codes</c> table and its composite index. The
    /// CASCADE foreign key removes a user's recovery codes alongside the user row. The
    /// index name matches the pgTAP-friendly convention used elsewhere in the schema.
    /// </summary>
    public override void Up()
    {
        Execute.Sql(@"
CREATE TABLE user_recovery_codes (
    id          uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     uuid        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    code_hash   text        NOT NULL,
    used_at     timestamptz NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);");

        Execute.Sql(
            "CREATE INDEX ix_user_recovery_codes_user_id_used_at "
            + "ON user_recovery_codes (user_id, used_at);"
        );
    }

    /// <summary>
    /// Drops the <c>user_recovery_codes</c> table. The composite index is dropped
    /// implicitly with the table.
    /// </summary>
    public override void Down()
    {
        Delete.Table("user_recovery_codes");
    }
}
