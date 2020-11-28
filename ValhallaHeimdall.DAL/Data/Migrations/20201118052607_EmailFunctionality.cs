using Microsoft.EntityFrameworkCore.Migrations;

namespace ValhallaHeimdall.DAL.Data.Migrations
{
    public partial class EmailFunctionality : Migration
    {
        protected override void Up( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.AddColumn<string>( "RecipientId", "Notifications", nullable: true );

            migrationBuilder.AddColumn<string>( "SenderId", "Notifications", nullable: true );

            migrationBuilder.AddColumn<bool>( "Viewed", "Notifications", nullable: false, defaultValue: false );

            migrationBuilder.CreateIndex( "IX_Notifications_RecipientId", "Notifications", "RecipientId" );

            migrationBuilder.AddForeignKey(
                                           "FK_Notifications_AspNetUsers_RecipientId",
                                           "Notifications",
                                           "RecipientId",
                                           "AspNetUsers",
                                           principalColumn: "Id",
                                           onDelete: ReferentialAction.Restrict );
        }

        protected override void Down( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.DropForeignKey( "FK_Notifications_AspNetUsers_RecipientId", "Notifications" );

            migrationBuilder.DropIndex( "IX_Notifications_RecipientId", "Notifications" );

            migrationBuilder.DropColumn( "RecipientId", "Notifications" );

            migrationBuilder.DropColumn( "SenderId", "Notifications" );

            migrationBuilder.DropColumn( "Viewed", "Notifications" );
        }
    }
}
