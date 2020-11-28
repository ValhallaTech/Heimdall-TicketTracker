using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ValhallaHeimdall.Data.Migrations
{
    public partial class Init01 : Migration
    {
        protected override void Up( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.AlterColumn<string>( name: "Name",
                                                  table: "AspNetUserTokens",
                                                  nullable: false,
                                                  oldClrType: typeof( string ),
                                                  oldType: "nvarchar(128)",
                                                  oldMaxLength: 128 );

            migrationBuilder.AlterColumn<string>( name: "LoginProvider",
                                                  table: "AspNetUserTokens",
                                                  nullable: false,
                                                  oldClrType: typeof( string ),
                                                  oldType: "nvarchar(128)",
                                                  oldMaxLength: 128 );

            migrationBuilder.AddColumn<string>( name: "FirstName",
                                                table: "AspNetUsers",
                                                maxLength: 50,
                                                nullable: false,
                                                defaultValue: string.Empty );

            migrationBuilder.AddColumn<byte[]>( name: "ImageData",
                                                table: "AspNetUsers",
                                                nullable: true );

            migrationBuilder.AddColumn<string>( name: "ImagePath",
                                                table: "AspNetUsers",
                                                nullable: true );

            migrationBuilder.AddColumn<string>( name: "LastName",
                                                table: "AspNetUsers",
                                                maxLength: 50,
                                                nullable: false,
                                                defaultValue: string.Empty );

            migrationBuilder.AlterColumn<string>( name: "ProviderKey",
                                                  table: "AspNetUserLogins",
                                                  nullable: false,
                                                  oldClrType: typeof( string ),
                                                  oldType: "nvarchar(128)",
                                                  oldMaxLength: 128 );

            migrationBuilder.AlterColumn<string>( name: "LoginProvider",
                                                  table: "AspNetUserLogins",
                                                  nullable: false,
                                                  oldClrType: typeof( string ),
                                                  oldType: "nvarchar(128)",
                                                  oldMaxLength: 128 );

            migrationBuilder.CreateTable( name: "Projects",
                                          columns: table => new
                                                            {
                                                                Id = table.Column<int>( nullable: false )
                                                                          .Annotation( "SqlServer:Identity", "1, 1" ),
                                                                Name =
                                                                    table.Column<string>( maxLength: 50,
                                                                        nullable: false ),
                                                                ImagePath = table.Column<string>( nullable: true ),
                                                                ImageData = table.Column<byte[]>( nullable: true )
                                                            },
                                          constraints: table => { table.PrimaryKey( "PK_Projects", x => x.Id ); } );

            migrationBuilder.CreateTable( name: "TicketPriorities",
                                          columns: table => new
                                                            {
                                                                Id = table.Column<int>( nullable: false )
                                                                          .Annotation( "SqlServer:Identity", "1, 1" ),
                                                                Name = table.Column<string>( nullable: true )
                                                            },
                                          constraints: table =>
                                                       {
                                                           table.PrimaryKey( "PK_TicketPriorities", x => x.Id );
                                                       } );

            migrationBuilder.CreateTable( name: "TicketStatuses",
                                          columns: table => new
                                                            {
                                                                Id = table.Column<int>( nullable: false )
                                                                          .Annotation( "SqlServer:Identity", "1, 1" ),
                                                                Name = table.Column<string>( nullable: true )
                                                            },
                                          constraints: table =>
                                                       {
                                                           table.PrimaryKey( "PK_TicketStatuses", x => x.Id );
                                                       } );

            migrationBuilder.CreateTable( name: "TicketTypes",
                                          columns: table => new
                                                            {
                                                                Id = table.Column<int>( nullable: false )
                                                                          .Annotation( "SqlServer:Identity", "1, 1" ),
                                                                Name = table.Column<string>( nullable: true )
                                                            },
                                          constraints: table => { table.PrimaryKey( "PK_TicketTypes", x => x.Id ); } );

            migrationBuilder.CreateTable( name: "ProjectUsers",
                                          columns: table => new
                                                            {
                                                                UserId    = table.Column<string>( nullable: false ),
                                                                ProjectId = table.Column<int>( nullable: false )
                                                            },
                                          constraints: table =>
                                                       {
                                                           table.PrimaryKey( "PK_ProjectUsers",
                                                                             x => new { x.ProjectId, x.UserId } );
                                                           table.ForeignKey( name: "FK_ProjectUsers_Projects_ProjectId",
                                                                             column: x => x.ProjectId,
                                                                             principalTable: "Projects",
                                                                             principalColumn: "Id",
                                                                             onDelete: ReferentialAction.Cascade );
                                                           table.ForeignKey( name: "FK_ProjectUsers_AspNetUsers_UserId",
                                                                             column: x => x.UserId,
                                                                             principalTable: "AspNetUsers",
                                                                             principalColumn: "Id",
                                                                             onDelete: ReferentialAction.Cascade );
                                                       } );

            migrationBuilder.CreateTable( name: "Tickets",
                                          columns: table => new
                                                            {
                                                                Id = table.Column<int>( nullable: false )
                                                                          .Annotation( "SqlServer:Identity", "1, 1" ),
                                                                Title =
                                                                    table.Column<string>( maxLength: 50,
                                                                        nullable: false ),
                                                                Description = table.Column<string>( nullable: false ),
                                                                Created =
                                                                    table.Column<DateTimeOffset>( nullable: false ),
                                                                Updated =
                                                                    table.Column<DateTimeOffset>( nullable: true ),
                                                                ProjectId = table.Column<int>( nullable: false ),
                                                                TicketTypeId = table.Column<int>( nullable: false ),
                                                                TicketPriorityId = table.Column<int>( nullable: false ),
                                                                TicketStatusId = table.Column<int>( nullable: false ),
                                                                OwnerUserId = table.Column<string>( nullable: true ),
                                                                DeveloperUserId = table.Column<string>( nullable: true )
                                                            },
                                          constraints: table =>
                                                       {
                                                           table.PrimaryKey( "PK_Tickets", x => x.Id );
                                                           table.ForeignKey( name:
                                                                             "FK_Tickets_AspNetUsers_DeveloperUserId",
                                                                             column: x => x.DeveloperUserId,
                                                                             principalTable: "AspNetUsers",
                                                                             principalColumn: "Id",
                                                                             onDelete: ReferentialAction.Restrict );
                                                           table.ForeignKey( name: "FK_Tickets_AspNetUsers_OwnerUserId",
                                                                             column: x => x.OwnerUserId,
                                                                             principalTable: "AspNetUsers",
                                                                             principalColumn: "Id",
                                                                             onDelete: ReferentialAction.Restrict );
                                                           table.ForeignKey( name: "FK_Tickets_Projects_ProjectId",
                                                                             column: x => x.ProjectId,
                                                                             principalTable: "Projects",
                                                                             principalColumn: "Id",
                                                                             onDelete: ReferentialAction.Cascade );
                                                           table.ForeignKey( name:
                                                                             "FK_Tickets_TicketPriorities_TicketPriorityId",
                                                                             column: x => x.TicketPriorityId,
                                                                             principalTable: "TicketPriorities",
                                                                             principalColumn: "Id",
                                                                             onDelete: ReferentialAction.Cascade );
                                                           table.ForeignKey( name:
                                                                             "FK_Tickets_TicketStatuses_TicketStatusId",
                                                                             column: x => x.TicketStatusId,
                                                                             principalTable: "TicketStatuses",
                                                                             principalColumn: "Id",
                                                                             onDelete: ReferentialAction.Cascade );
                                                           table.ForeignKey( name:
                                                                             "FK_Tickets_TicketTypes_TicketTypeId",
                                                                             column: x => x.TicketTypeId,
                                                                             principalTable: "TicketTypes",
                                                                             principalColumn: "Id",
                                                                             onDelete: ReferentialAction.Cascade );
                                                       } );

            migrationBuilder.CreateTable( name: "Notifications",
                                          columns: table => new
                                                            {
                                                                Id = table.Column<int>( nullable: false )
                                                                          .Annotation( "SqlServer:Identity", "1, 1" ),
                                                                TicketId    = table.Column<int>( nullable: false ),
                                                                Description = table.Column<string>( nullable: true ),
                                                                Created =
                                                                    table.Column<DateTimeOffset>( nullable: false ),
                                                                UserId = table.Column<string>( nullable: true )
                                                            },
                                          constraints: table =>
                                                       {
                                                           table.PrimaryKey( "PK_Notifications", x => x.Id );
                                                           table.ForeignKey( name: "FK_Notifications_Tickets_TicketId",
                                                                             column: x => x.TicketId,
                                                                             principalTable: "Tickets",
                                                                             principalColumn: "Id",
                                                                             onDelete: ReferentialAction.Cascade );
                                                           table.ForeignKey( name:
                                                                             "FK_Notifications_AspNetUsers_UserId",
                                                                             column: x => x.UserId,
                                                                             principalTable: "AspNetUsers",
                                                                             principalColumn: "Id",
                                                                             onDelete: ReferentialAction.Restrict );
                                                       } );

            migrationBuilder.CreateTable( name: "TicketAttachments",
                                          columns: table => new
                                                            {
                                                                Id = table.Column<int>( nullable: false )
                                                                          .Annotation( "SqlServer:Identity", "1, 1" ),
                                                                FilePath    = table.Column<string>( nullable: true ),
                                                                FileData    = table.Column<byte[]>( nullable: false ),
                                                                Description = table.Column<string>( nullable: true ),
                                                                Created =
                                                                    table.Column<DateTimeOffset>( nullable: false ),
                                                                TicketId = table.Column<int>( nullable: false ),
                                                                UserId   = table.Column<string>( nullable: true )
                                                            },
                                          constraints: table =>
                                                       {
                                                           table.PrimaryKey( "PK_TicketAttachments", x => x.Id );
                                                           table.ForeignKey( name:
                                                                             "FK_TicketAttachments_Tickets_TicketId",
                                                                             column: x => x.TicketId,
                                                                             principalTable: "Tickets",
                                                                             principalColumn: "Id",
                                                                             onDelete: ReferentialAction.Cascade );
                                                           table.ForeignKey( name:
                                                                             "FK_TicketAttachments_AspNetUsers_UserId",
                                                                             column: x => x.UserId,
                                                                             principalTable: "AspNetUsers",
                                                                             principalColumn: "Id",
                                                                             onDelete: ReferentialAction.Restrict );
                                                       } );

            migrationBuilder.CreateTable( name: "TicketComments",
                                          columns: table => new
                                                            {
                                                                Id = table.Column<int>( nullable: false )
                                                                          .Annotation( "SqlServer:Identity", "1, 1" ),
                                                                Comment = table.Column<string>( nullable: true ),
                                                                Created =
                                                                    table.Column<DateTimeOffset>( nullable: false ),
                                                                TicketId = table.Column<int>( nullable: false ),
                                                                UserId   = table.Column<string>( nullable: true )
                                                            },
                                          constraints: table =>
                                                       {
                                                           table.PrimaryKey( "PK_TicketComments", x => x.Id );
                                                           table.ForeignKey( name: "FK_TicketComments_Tickets_TicketId",
                                                                             column: x => x.TicketId,
                                                                             principalTable: "Tickets",
                                                                             principalColumn: "Id",
                                                                             onDelete: ReferentialAction.Cascade );
                                                           table.ForeignKey( name:
                                                                             "FK_TicketComments_AspNetUsers_UserId",
                                                                             column: x => x.UserId,
                                                                             principalTable: "AspNetUsers",
                                                                             principalColumn: "Id",
                                                                             onDelete: ReferentialAction.Restrict );
                                                       } );

            migrationBuilder.CreateTable( name: "TicketHistories",
                                          columns: table => new
                                                            {
                                                                Id = table.Column<int>( nullable: false )
                                                                          .Annotation( "SqlServer:Identity", "1, 1" ),
                                                                TicketId = table.Column<int>( nullable: false ),
                                                                Property = table.Column<string>( nullable: true ),
                                                                OldValue = table.Column<string>( nullable: true ),
                                                                NewValue = table.Column<string>( nullable: true ),
                                                                Created =
                                                                    table.Column<DateTimeOffset>( nullable: false ),
                                                                UserId = table.Column<string>( nullable: true )
                                                            },
                                          constraints: table =>
                                                       {
                                                           table.PrimaryKey( "PK_TicketHistories", x => x.Id );
                                                           table.ForeignKey( name:
                                                                             "FK_TicketHistories_Tickets_TicketId",
                                                                             column: x => x.TicketId,
                                                                             principalTable: "Tickets",
                                                                             principalColumn: "Id",
                                                                             onDelete: ReferentialAction.Cascade );
                                                           table.ForeignKey( name:
                                                                             "FK_TicketHistories_AspNetUsers_UserId",
                                                                             column: x => x.UserId,
                                                                             principalTable: "AspNetUsers",
                                                                             principalColumn: "Id",
                                                                             onDelete: ReferentialAction.Restrict );
                                                       } );

            migrationBuilder.CreateIndex( name: "IX_Notifications_TicketId",
                                          table: "Notifications",
                                          column: "TicketId" );

            migrationBuilder.CreateIndex( name: "IX_Notifications_UserId",
                                          table: "Notifications",
                                          column: "UserId" );

            migrationBuilder.CreateIndex( name: "IX_ProjectUsers_UserId",
                                          table: "ProjectUsers",
                                          column: "UserId" );

            migrationBuilder.CreateIndex( name: "IX_TicketAttachments_TicketId",
                                          table: "TicketAttachments",
                                          column: "TicketId" );

            migrationBuilder.CreateIndex( name: "IX_TicketAttachments_UserId",
                                          table: "TicketAttachments",
                                          column: "UserId" );

            migrationBuilder.CreateIndex( name: "IX_TicketComments_TicketId",
                                          table: "TicketComments",
                                          column: "TicketId" );

            migrationBuilder.CreateIndex( name: "IX_TicketComments_UserId",
                                          table: "TicketComments",
                                          column: "UserId" );

            migrationBuilder.CreateIndex( name: "IX_TicketHistories_TicketId",
                                          table: "TicketHistories",
                                          column: "TicketId" );

            migrationBuilder.CreateIndex( name: "IX_TicketHistories_UserId",
                                          table: "TicketHistories",
                                          column: "UserId" );

            migrationBuilder.CreateIndex( name: "IX_Tickets_DeveloperUserId",
                                          table: "Tickets",
                                          column: "DeveloperUserId" );

            migrationBuilder.CreateIndex( name: "IX_Tickets_OwnerUserId",
                                          table: "Tickets",
                                          column: "OwnerUserId" );

            migrationBuilder.CreateIndex( name: "IX_Tickets_ProjectId",
                                          table: "Tickets",
                                          column: "ProjectId" );

            migrationBuilder.CreateIndex( name: "IX_Tickets_TicketPriorityId",
                                          table: "Tickets",
                                          column: "TicketPriorityId" );

            migrationBuilder.CreateIndex( name: "IX_Tickets_TicketStatusId",
                                          table: "Tickets",
                                          column: "TicketStatusId" );

            migrationBuilder.CreateIndex( name: "IX_Tickets_TicketTypeId",
                                          table: "Tickets",
                                          column: "TicketTypeId" );
        }

        protected override void Down( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.DropTable( name: "Notifications" );

            migrationBuilder.DropTable( name: "ProjectUsers" );

            migrationBuilder.DropTable( name: "TicketAttachments" );

            migrationBuilder.DropTable( name: "TicketComments" );

            migrationBuilder.DropTable( name: "TicketHistories" );

            migrationBuilder.DropTable( name: "Tickets" );

            migrationBuilder.DropTable( name: "Projects" );

            migrationBuilder.DropTable( name: "TicketPriorities" );

            migrationBuilder.DropTable( name: "TicketStatuses" );

            migrationBuilder.DropTable( name: "TicketTypes" );

            migrationBuilder.DropColumn( name: "FirstName",
                                         table: "AspNetUsers" );

            migrationBuilder.DropColumn( name: "ImageData",
                                         table: "AspNetUsers" );

            migrationBuilder.DropColumn( name: "ImagePath",
                                         table: "AspNetUsers" );

            migrationBuilder.DropColumn( name: "LastName",
                                         table: "AspNetUsers" );

            migrationBuilder.AlterColumn<string>( name: "Name",
                                                  table: "AspNetUserTokens",
                                                  type: "nvarchar(128)",
                                                  maxLength: 128,
                                                  nullable: false,
                                                  oldClrType: typeof( string ) );

            migrationBuilder.AlterColumn<string>( name: "LoginProvider",
                                                  table: "AspNetUserTokens",
                                                  type: "nvarchar(128)",
                                                  maxLength: 128,
                                                  nullable: false,
                                                  oldClrType: typeof( string ) );

            migrationBuilder.AlterColumn<string>( name: "ProviderKey",
                                                  table: "AspNetUserLogins",
                                                  type: "nvarchar(128)",
                                                  maxLength: 128,
                                                  nullable: false,
                                                  oldClrType: typeof( string ) );

            migrationBuilder.AlterColumn<string>( name: "LoginProvider",
                                                  table: "AspNetUserLogins",
                                                  type: "nvarchar(128)",
                                                  maxLength: 128,
                                                  nullable: false,
                                                  oldClrType: typeof( string ) );
        }
    }
}
