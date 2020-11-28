using Microsoft.EntityFrameworkCore.Migrations;

namespace ValhallaHeimdall.DAL.Data.Migrations
{
    public partial class Functionality01 : Migration
    {
        protected override void Up( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.AddColumn<string>( "AddProjectUsers", "ProjectUsers", nullable: true );

            migrationBuilder.AddColumn<string>( "RemoveProjectUsers", "ProjectUsers", nullable: true );
        }

        protected override void Down( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.DropColumn( "AddProjectUsers", "ProjectUsers" );

            migrationBuilder.DropColumn( "RemoveProjectUsers", "ProjectUsers" );
        }
    }
}
