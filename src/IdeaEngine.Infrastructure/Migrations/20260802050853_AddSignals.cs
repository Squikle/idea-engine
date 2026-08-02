using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IdeaEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "signals",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    raw_item_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    audience = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    commercial_sentiment = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    novelty = table.Column<double>(type: "double precision", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signals", x => x.id);
                    table.ForeignKey(
                        name: "fk_signals_raw_items_raw_item_id",
                        column: x => x.raw_item_id,
                        principalTable: "raw_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_signals_created_at",
                table: "signals",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_signals_raw_item_id",
                table: "signals",
                column: "raw_item_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "signals");
        }
    }
}
