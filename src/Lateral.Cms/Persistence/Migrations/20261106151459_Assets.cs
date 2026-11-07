using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lateral.Cms.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Assets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DetectedType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ScannedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ScannerName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ScannerVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Verdict = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Signature = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UploadParts",
                columns: table => new
                {
                    UploadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PartNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadParts", x => new { x.UploadId, x.PartNumber });
                });

            migrationBuilder.CreateTable(
                name: "Uploads",
                columns: table => new
                {
                    UploadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DeclaredSize = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Uploads", x => x.UploadId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_Scan",
                table: "Assets",
                columns: new[] { "State", "NextAttemptAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_Sha256",
                table: "Assets",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_Uploads_ExpiresAt",
                table: "Uploads",
                column: "ExpiresAt");

            // The audit log is append-only: the schema itself refuses to change or remove a row.
            migrationBuilder.Sql(
                "CREATE TRIGGER \"AuditLog_no_update\" BEFORE UPDATE ON \"AuditLog\" BEGIN SELECT RAISE(ABORT, 'AuditLog is append-only'); END;");
            migrationBuilder.Sql(
                "CREATE TRIGGER \"AuditLog_no_delete\" BEFORE DELETE ON \"AuditLog\" BEGIN SELECT RAISE(ABORT, 'AuditLog is append-only'); END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"AuditLog_no_update\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"AuditLog_no_delete\";");

            migrationBuilder.DropTable(
                name: "Assets");

            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropTable(
                name: "UploadParts");

            migrationBuilder.DropTable(
                name: "Uploads");
        }
    }
}
