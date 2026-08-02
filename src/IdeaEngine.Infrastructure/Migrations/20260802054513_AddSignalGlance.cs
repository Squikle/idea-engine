using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdeaEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSignalGlance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "glance",
                table: "signals",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "glance",
                table: "signals");
        }
    }
}
