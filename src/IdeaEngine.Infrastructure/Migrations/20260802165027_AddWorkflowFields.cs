using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdeaEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "progress_message_id",
                table: "jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "notes_json",
                table: "ideas",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "playbook",
                table: "ideas",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "verified",
                table: "ideas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_ideas_verified",
                table: "ideas",
                column: "verified");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_ideas_verified",
                table: "ideas");

            migrationBuilder.DropColumn(
                name: "progress_message_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "notes_json",
                table: "ideas");

            migrationBuilder.DropColumn(
                name: "playbook",
                table: "ideas");

            migrationBuilder.DropColumn(
                name: "verified",
                table: "ideas");
        }
    }
}
