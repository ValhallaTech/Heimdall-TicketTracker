using Microsoft.EntityFrameworkCore.Migrations;

namespace ValhallaHeimdall.Data.Migrations
{
    public partial class Functionality01 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AddProjectUsers",
                table: "ProjectUsers",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoveProjectUsers",
                table: "ProjectUsers",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddProjectUsers",
                table: "ProjectUsers");

            migrationBuilder.DropColumn(
                name: "RemoveProjectUsers",
                table: "ProjectUsers");
        }
    }
}
