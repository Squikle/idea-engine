using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace IdeaEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "ai_ledger",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    stage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    tokens_in = table.Column<long>(type: "bigint", nullable: false),
                    tokens_out = table.Column<long>(type: "bigint", nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric(10,6)", precision: 10, scale: 6, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_ledger", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pipeline_runs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    stage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    items_in = table.Column<int>(type: "integer", nullable: false),
                    items_out = table.Column<int>(type: "integer", nullable: false),
                    errors = table.Column<int>(type: "integer", nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric(10,6)", precision: 10, scale: 6, nullable: false),
                    notes = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pipeline_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "raw_items",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source = table.Column<int>(type: "integer", nullable: false),
                    external_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    title = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    body = table.Column<string>(type: "text", nullable: true),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    author = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    community = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    score = table.Column<long>(type: "bigint", nullable: false),
                    comment_count = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    comments_json = table.Column<string>(type: "jsonb", nullable: true),
                    raw_payload_json = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(384)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_raw_items", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_ledger_day_stage",
                table: "ai_ledger",
                columns: new[] { "day", "stage" });

            migrationBuilder.CreateIndex(
                name: "ix_pipeline_runs_started_at",
                table: "pipeline_runs",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_raw_items_content_hash",
                table: "raw_items",
                column: "content_hash");

            migrationBuilder.CreateIndex(
                name: "ix_raw_items_fetched_at",
                table: "raw_items",
                column: "fetched_at");

            migrationBuilder.CreateIndex(
                name: "ix_raw_items_source_external_id",
                table: "raw_items",
                columns: new[] { "source", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_raw_items_status",
                table: "raw_items",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_ledger");

            migrationBuilder.DropTable(
                name: "pipeline_runs");

            migrationBuilder.DropTable(
                name: "raw_items");
        }
    }
}
