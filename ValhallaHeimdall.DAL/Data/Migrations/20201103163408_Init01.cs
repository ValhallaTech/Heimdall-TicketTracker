using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ValhallaHeimdall.DAL.Data.Migrations
{
    public partial class Init01 : Migration
    {
        protected override void Up( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.AlterColumn<string>(
                                                 "Name",
                                                 "AspNetUserTokens",
                                                 nullable: false,
                                                 oldClrType: typeof( string ),
                                                 oldType: "nvarchar(128)",
                                                 oldMaxLength: 128 );

            migrationBuilder.AlterColumn<string>(
                                                 "LoginProvider",
                                                 "AspNetUserTokens",
                                                 nullable: false,
                                                 oldClrType: typeof( string ),
                                                 oldType: "nvarchar(128)",
                                                 oldMaxLength: 128 );

            migrationBuilder.AddColumn<string>(
                                               "FirstName",
                                               "AspNetUsers",
                                               maxLength: 50,
                                               nullable: false,
                                               defaultValue: string.Empty );

            migrationBuilder.AddColumn<byte[]>( "ImageData", "AspNetUsers", nullable: true );

            migrationBuilder.AddColumn<string>( "ImagePath", "AspNetUsers", nullable: true );

            migrationBuilder.AddColumn<string>(
                                               "LastName",
                                               "AspNetUsers",
                                               maxLength: 50,
                                               nullable: false,
                                               defaultValue: string.Empty );

            migrationBuilder.AlterColumn<string>(
                                                 "ProviderKey",
                                                 "AspNetUserLogins",
                                                 nullable: false,
                                                 oldClrType: typeof( string ),
                                                 oldType: "nvarchar(128)",
                                                 oldMaxLength: 128 );

            migrationBuilder.AlterColumn<string>(
                                                 "LoginProvider",
                                                 "AspNetUserLogins",
                                                 nullable: false,
                                                 oldClrType: typeof( string ),
                                                 oldType: "nvarchar(128)",
                                                 oldMaxLength: 128 );

            migrationBuilder.CreateTable(
                                         "Projects",
                                         table => new
                                                  {
                                                      Id = table.Column<int>( nullable: false )
                                                                .Annotation( "SqlServer:Identity", "1, 1" ),
                                                      Name = table.Column<string>( maxLength: 50, nullable: false ),
                                                      ImagePath = table.Column<string>( nullable: true ),
                                                      ImageData = table.Column<byte[]>( nullable: true )
                                                  },
                                         constraints: table => { table.PrimaryKey( "PK_Projects", x => x.Id ); } );

            migrationBuilder.CreateTable(
                                         "TicketPriorities",
                                         table => new
                                                  {
                                                      Id = table.Column<int>( nullable: false )
                                                                .Annotation( "SqlServer:Identity", "1, 1" ),
                                                      Name = table.Column<string>( nullable: true )
                                                  },
                                         constraints: table =>
                                                      {
                                                          table.PrimaryKey( "PK_TicketPriorities", x => x.Id );
                                                      } );

            migrationBuilder.CreateTable(
                                         "TicketStatuses",
                                         table => new
                                                  {
                                                      Id = table.Column<int>( nullable: false )
                                                                .Annotation( "SqlServer:Identity", "1, 1" ),
                                                      Name = table.Column<string>( nullable: true )
                                                  },
                                         constraints: table =>
                                                      {
                                                          table.PrimaryKey( "PK_TicketStatuses", x => x.Id );
                                                      } );

            migrationBuilder.CreateTable(
                                         "TicketTypes",
                                         table => new
                                                  {
                                                      Id = table.Column<int>( nullable: false )
                                                                .Annotation( "SqlServer:Identity", "1, 1" ),
                                                      Name = table.Column<string>( nullable: true )
                                                  },
                                         constraints: table => { table.PrimaryKey( "PK_TicketTypes", x => x.Id ); } );

            migrationBuilder.CreateTable(
                                         "ProjectUsers",
                                         table => new
                                                  {
                                                      UserId    = table.Column<string>( nullable: false ),
                                                      ProjectId = table.Column<int>( nullable: false )
                                                  },
                                         constraints: table =>
                                                      {
                                                          table.PrimaryKey(
                                                                           "PK_ProjectUsers",
                                                                           x => new { x.ProjectId, x.UserId } );
                                                          table.ForeignKey(
                                                                           "FK_ProjectUsers_Projects_ProjectId",
                                                                           x => x.ProjectId,
                                                                           "Projects",
                                                                           "Id",
                                                                           onDelete: ReferentialAction.Cascade );
                                                          table.ForeignKey(
                                                                           "FK_ProjectUsers_AspNetUsers_UserId",
                                                                           x => x.UserId,
                                                                           "AspNetUsers",
                                                                           "Id",
                                                                           onDelete: ReferentialAction.Cascade );
                                                      } );

            migrationBuilder.CreateTable(
                                         "Tickets",
                                         table => new
                                                  {
                                                      Id =
                                                          table.Column<int>( nullable: false )
                                                               .Annotation( "SqlServer:Identity", "1, 1" ),
                                                      Title = table.Column<string>( maxLength: 50, nullable: false ),
                                                      Description = table.Column<string>( nullable: false ),
                                                      Created = table.Column<DateTimeOffset>( nullable: false ),
                                                      Updated = table.Column<DateTimeOffset>( nullable: true ),
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
                                                          table.ForeignKey(
                                                                           "FK_Tickets_AspNetUsers_DeveloperUserId",
                                                                           x => x.DeveloperUserId,
                                                                           "AspNetUsers",
                                                                           "Id",
                                                                           onDelete: ReferentialAction.Restrict );
                                                          table.ForeignKey(
                                                                           "FK_Tickets_AspNetUsers_OwnerUserId",
                                                                           x => x.OwnerUserId,
                                                                           "AspNetUsers",
                                                                           "Id",
                                                                           onDelete: ReferentialAction.Restrict );
                                                          table.ForeignKey(
                                                                           "FK_Tickets_Projects_ProjectId",
                                                                           x => x.ProjectId,
                                                                           "Projects",
                                                                           "Id",
                                                                           onDelete: ReferentialAction.Cascade );
                                                          table.ForeignKey(
                                                                           "FK_Tickets_TicketPriorities_TicketPriorityId",
                                                                           x => x.TicketPriorityId,
                                                                           "TicketPriorities",
                                                                           "Id",
                                                                           onDelete: ReferentialAction.Cascade );
                                                          table.ForeignKey(
                                                                           "FK_Tickets_TicketStatuses_TicketStatusId",
                                                                           x => x.TicketStatusId,
                                                                           "TicketStatuses",
                                                                           "Id",
                                                                           onDelete: ReferentialAction.Cascade );
                                                          table.ForeignKey(
                                                                           "FK_Tickets_TicketTypes_TicketTypeId",
                                                                           x => x.TicketTypeId,
                                                                           "TicketTypes",
                                                                           "Id",
                                                                           onDelete: ReferentialAction.Cascade );
                                                      } );

            migrationBuilder.CreateTable(
                                         "Notifications",
                                         table => new
                                                  {
                                                      Id = table.Column<int>( nullable: false )
                                                                .Annotation( "SqlServer:Identity", "1, 1" ),
                                                      TicketId    = table.Column<int>( nullable: false ),
                                                      Description = table.Column<string>( nullable: true ),
                                                      Created     = table.Column<DateTimeOffset>( nullable: false ),
                                                      UserId      = table.Column<string>( nullable: true )
                                                  },
                                         constraints: table =>
                                                      {
                                                          table.PrimaryKey( "PK_Notifications", x => x.Id );
                                                          table.ForeignKey(
                                                                           "FK_Notifications_Tickets_TicketId",
                                                                           x => x.TicketId,
                                                                           "Tickets",
                                                                           "Id",
                                                                           onDelete: ReferentialAction.Cascade );
                                                          table.ForeignKey(
                                                                           "FK_Notifications_AspNetUsers_UserId",
                                                                           x => x.UserId,
                                                                           "AspNetUsers",
                                                                           "Id",
                                                                           onDelete: ReferentialAction.Restrict );
                                                      } );

            migrationBuilder.CreateTable(
                                         "TicketAttachments",
                                         table => new
                                                  {
                                                      Id = table.Column<int>( nullable: false )
                                                                .Annotation( "SqlServer:Identity", "1, 1" ),
                                                      FilePath    = table.Column<string>( nullable: true ),
                                                      FileData    = table.Column<byte[]>( nullable: false ),
                                                      Description = table.Column<string>( nullable: true ),
                                                      Created     = table.Column<DateTimeOffset>( nullable: false ),
                                                      TicketId    = table.Column<int>( nullable: false ),
                                                      UserId      = table.Column<string>( nullable: true )
                                                  },
                                         constraints: table =>
                                                      {
                                                          table.PrimaryKey( "PK_TicketAttachments", x => x.Id );
                                                          table.ForeignKey(
                                                                           "FK_TicketAttachments_Tickets_TicketId",
                                                                           x => x.TicketId,
                                                                           "Tickets",
                                                                           "Id",
                                                                           onDelete: ReferentialAction.Cascade );
                                                          table.ForeignKey(
                                                                           "FK_TicketAttachments_AspNetUsers_UserId",
                                                                           x => x.UserId,
                                                                           "AspNetUsers",
                                                                           "Id",
                                                                           onDelete: ReferentialAction.Restrict );
                                                      } );

            migrationBuilder.CreateTable(
                                         "TicketComments",
                                         table => new
                                                  {
                                                      Id = table.Column<int>( nullable: false )
                                                                .Annotation( "SqlServer:Identity", "1, 1" ),
                                                      Comment  = table.Column<string>( nullable: true ),
                                                      Created  = table.Column<DateTimeOffset>( nullable: false ),
                                                      TicketId = table.Column<int>( nullable: false ),
                                                      UserId   = table.Column<string>( nullable: true )
                                                  },
                                         constraints: table =>
                                                      {
                                                          table.PrimaryKey( "PK_TicketComments", x => x.Id );
                                                          table.ForeignKey(
                                                                           "FK_TicketComments_Tickets_TicketId",
                                                                           x => x.TicketId,
                                                                           "Tickets",
                                                                           "Id",
                                                                           onDelete: ReferentialAction.Cascade );
                                                          table.ForeignKey(
                                                                           "FK_TicketComments_AspNetUsers_UserId",
                                                                           x => x.UserId,
                                                                           "AspNetUsers",
                                                                           "Id",
                                                                           onDelete: ReferentialAction.Restrict );
                                                      } );

            migrationBuilder.CreateTable(
                                         "TicketHistories",
                                         table => new
                                                  {
                                                      Id = table.Column<int>( nullable: false )
                                                                .Annotation( "SqlServer:Identity", "1, 1" ),
                                                      TicketId = table.Column<int>( nullable: false ),
                                                      Property = table.Column<string>( nullable: true ),
                                                      OldValue = table.Column<string>( nullable: true ),
                                                      NewValue = table.Column<string>( nullable: true ),
                                                      Created  = table.Column<DateTimeOffset>( nullable: false ),
                                                      UserId   = table.Column<string>( nullable: true )
                                                  },
                                         constraints: table =>
                                                      {
                                                          table.PrimaryKey( "PK_TicketHistories", x => x.Id );
                                                          table.ForeignKey(
                                                                           "FK_TicketHistories_Tickets_TicketId",
                                                                           x => x.TicketId,
                                                                           "Tickets",
                                                                           "Id",
                                                                           onDelete: ReferentialAction.Cascade );
                                                          table.ForeignKey(
                                                                           "FK_TicketHistories_AspNetUsers_UserId",
                                                                           x => x.UserId,
                                                                           "AspNetUsers",
                                                                           "Id",
                                                                           onDelete: ReferentialAction.Restrict );
                                                      } );

            migrationBuilder.CreateIndex( "IX_Notifications_TicketId", "Notifications", "TicketId" );

            migrationBuilder.CreateIndex( "IX_Notifications_UserId", "Notifications", "UserId" );

            migrationBuilder.CreateIndex( "IX_ProjectUsers_UserId", "ProjectUsers", "UserId" );

            migrationBuilder.CreateIndex( "IX_TicketAttachments_TicketId", "TicketAttachments", "TicketId" );

            migrationBuilder.CreateIndex( "IX_TicketAttachments_UserId", "TicketAttachments", "UserId" );

            migrationBuilder.CreateIndex( "IX_TicketComments_TicketId", "TicketComments", "TicketId" );

            migrationBuilder.CreateIndex( "IX_TicketComments_UserId", "TicketComments", "UserId" );

            migrationBuilder.CreateIndex( "IX_TicketHistories_TicketId", "TicketHistories", "TicketId" );

            migrationBuilder.CreateIndex( "IX_TicketHistories_UserId", "TicketHistories", "UserId" );

            migrationBuilder.CreateIndex( "IX_Tickets_DeveloperUserId", "Tickets", "DeveloperUserId" );

            migrationBuilder.CreateIndex( "IX_Tickets_OwnerUserId", "Tickets", "OwnerUserId" );

            migrationBuilder.CreateIndex( "IX_Tickets_ProjectId", "Tickets", "ProjectId" );

            migrationBuilder.CreateIndex( "IX_Tickets_TicketPriorityId", "Tickets", "TicketPriorityId" );

            migrationBuilder.CreateIndex( "IX_Tickets_TicketStatusId", "Tickets", "TicketStatusId" );

            migrationBuilder.CreateIndex( "IX_Tickets_TicketTypeId", "Tickets", "TicketTypeId" );
        }

        protected override void Down( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.DropTable( "Notifications" );

            migrationBuilder.DropTable( "ProjectUsers" );

            migrationBuilder.DropTable( "TicketAttachments" );

            migrationBuilder.DropTable( "TicketComments" );

            migrationBuilder.DropTable( "TicketHistories" );

            migrationBuilder.DropTable( "Tickets" );

            migrationBuilder.DropTable( "Projects" );

            migrationBuilder.DropTable( "TicketPriorities" );

            migrationBuilder.DropTable( "TicketStatuses" );

            migrationBuilder.DropTable( "TicketTypes" );

            migrationBuilder.DropColumn( "FirstName", "AspNetUsers" );

            migrationBuilder.DropColumn( "ImageData", "AspNetUsers" );

            migrationBuilder.DropColumn( "ImagePath", "AspNetUsers" );

            migrationBuilder.DropColumn( "LastName", "AspNetUsers" );

            migrationBuilder.AlterColumn<string>(
                                                 "Name",
                                                 "AspNetUserTokens",
                                                 "nvarchar(128)",
                                                 maxLength: 128,
                                                 nullable: false,
                                                 oldClrType: typeof( string ) );

            migrationBuilder.AlterColumn<string>(
                                                 "LoginProvider",
                                                 "AspNetUserTokens",
                                                 "nvarchar(128)",
                                                 maxLength: 128,
                                                 nullable: false,
                                                 oldClrType: typeof( string ) );

            migrationBuilder.AlterColumn<string>(
                                                 "ProviderKey",
                                                 "AspNetUserLogins",
                                                 "nvarchar(128)",
                                                 maxLength: 128,
                                                 nullable: false,
                                                 oldClrType: typeof( string ) );

            migrationBuilder.AlterColumn<string>(
                                                 "LoginProvider",
                                                 "AspNetUserLogins",
                                                 "nvarchar(128)",
                                                 maxLength: 128,
                                                 nullable: false,
                                                 oldClrType: typeof( string ) );
        }
    }
}
