using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IdeaEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddResearchArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "research_artifacts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    idea_id = table.Column<long>(type: "bigint", nullable: false),
                    report_id = table.Column<long>(type: "bigint", nullable: true),
                    kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_research_artifacts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_research_artifacts_idea_id",
                table: "research_artifacts",
                column: "idea_id");

            migrationBuilder.CreateIndex(
                name: "ix_research_artifacts_kind",
                table: "research_artifacts",
                column: "kind");

            migrationBuilder.CreateIndex(
                name: "ix_research_artifacts_report_id",
                table: "research_artifacts",
                column: "report_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "research_artifacts");
        }
    }
}
