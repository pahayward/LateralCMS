using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lateral.Cms.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssetLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssetLinks",
                columns: table => new
                {
                    AssetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetLinks", x => new { x.AssetId, x.EntityId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssetLinks_EntityId",
                table: "AssetLinks",
                column: "EntityId");

            // Records stored before this table existed: link each to the files it already names (only files that exist; from here on
            // the service writes a link for any file-id-shaped text as it applies a record).
            migrationBuilder.Sql(
                """
                INSERT OR IGNORE INTO "AssetLinks" ("AssetId", "EntityId")
                SELECT DISTINCT a."Id", e."Id"
                FROM "Entities" e, json_tree(e."Payload") t
                JOIN "Assets" a ON a."Id" = upper(t."value")
                WHERE t."type" = 'text';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssetLinks");
        }
    }
}
