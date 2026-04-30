using FluentMigrator;

namespace Heimdall.DAL.Migrations;

/// <summary>
/// Creates the <c>tickets</c> table with supporting indexes for status / priority filtering
/// and date-created sorting.
/// </summary>
[Migration(202604300001, "Create tickets")]
public class M202604300001_CreateTickets : Migration
{
    /// <inheritdoc />
    public override void Up()
    {
        Create
            .Table("tickets")
            .WithColumn("id")
            .AsInt32()
            .NotNullable()
            .PrimaryKey()
            .Identity()
            .WithColumn("title")
            .AsString(200)
            .NotNullable()
            .WithColumn("description")
            .AsCustom("text")
            .NotNullable()
            .WithColumn("status")
            .AsInt16()
            .NotNullable()
            .WithDefaultValue((short)0)
            .WithColumn("priority")
            .AsInt16()
            .NotNullable()
            .WithDefaultValue((short)1)
            .WithColumn("reporter")
            .AsString(100)
            .NotNullable()
            .WithColumn("assignee")
            .AsString(100)
            .Nullable()
            .WithColumn("date_created")
            .AsCustom("timestamptz")
            .NotNullable()
            .WithColumn("date_updated")
            .AsCustom("timestamptz")
            .NotNullable();

        // Domain CHECK constraints: status and priority are int-backed enums with values 0..3.
        // Enforcing the range at the database layer prevents out-of-band values from being
        // persisted by future ad-hoc SQL or buggy callers, and lets the planner exploit the
        // narrow domain.
        Execute.Sql(
            "ALTER TABLE tickets ADD CONSTRAINT ck_tickets_status_range "
                + "CHECK (status BETWEEN 0 AND 3);"
        );
        Execute.Sql(
            "ALTER TABLE tickets ADD CONSTRAINT ck_tickets_priority_range "
                + "CHECK (priority BETWEEN 0 AND 3);"
        );

        // Composite index supports the default DateCreated DESC list ordering and
        // also speeds up status-filtered listings.
        Create
            .Index("ix_tickets_status_date_created")
            .OnTable("tickets")
            .OnColumn("status")
            .Ascending()
            .OnColumn("date_created")
            .Descending();
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table("tickets");
    }
}
