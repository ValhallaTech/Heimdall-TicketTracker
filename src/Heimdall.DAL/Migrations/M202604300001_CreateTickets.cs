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
            .AsString(4000)
            .NotNullable()
            .WithColumn("status")
            .AsInt32()
            .NotNullable()
            .WithDefaultValue(0)
            .WithColumn("priority")
            .AsInt32()
            .NotNullable()
            .WithDefaultValue(1)
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

        // Composite index supports the default DateCreated DESC list ordering and
        // also speeds up status-filtered listings.
        Create
            .Index("ix_tickets_status_date_created")
            .OnTable("tickets")
            .OnColumn("status")
            .Ascending()
            .OnColumn("date_created")
            .Descending();

        // Index to support search/sort by priority.
        Create
            .Index("ix_tickets_priority")
            .OnTable("tickets")
            .OnColumn("priority")
            .Ascending();
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table("tickets");
    }
}
