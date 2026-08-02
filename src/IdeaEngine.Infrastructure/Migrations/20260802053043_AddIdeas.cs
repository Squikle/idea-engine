using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IdeaEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIdeas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ideas",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    thesis = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    effort_scale = table.Column<int>(type: "integer", nullable: false),
                    target_user = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    monetization = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    distribution_note = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    evidence_json = table.Column<string>(type: "jsonb", nullable: true),
                    scores_json = table.Column<string>(type: "jsonb", nullable: true),
                    skeptic_json = table.Column<string>(type: "jsonb", nullable: true),
                    builder_model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    skeptic_model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    cost_usd = table.Column<decimal>(type: "numeric(10,6)", precision: 10, scale: 6, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ideas", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ideas_created_at",
                table: "ideas",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_ideas_status",
                table: "ideas",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ideas");
        }
    }
}
