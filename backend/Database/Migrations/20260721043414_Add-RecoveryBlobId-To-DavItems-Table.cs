using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddRecoveryBlobIdToDavItemsTable : Migration
    {
        /// <summary>
        /// When a repaired DavItem is deleted, schedule its PAR2 recovery blob for
        /// cleanup by the existing BlobCleanupService, mirroring the NZB-blob trigger
        /// added in Add-NzbBlobId-And-NzbNames. Public so tests can exercise the
        /// trigger against an in-memory database.
        /// </summary>
        public const string CreateTriggerSql =
            """
            CREATE TRIGGER TR_DavItems_Delete_AddRecoveryBlobCleanup
            AFTER DELETE ON DavItems
            WHEN OLD.RecoveryBlobId IS NOT NULL
            BEGIN
                INSERT OR IGNORE INTO BlobCleanupItems (Id)
                VALUES (OLD.RecoveryBlobId);
            END
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RecoveryBlobId",
                table: "DavItems",
                type: "TEXT",
                nullable: true);
            migrationBuilder.Sql(CreateTriggerSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_DavItems_Delete_AddRecoveryBlobCleanup");
            migrationBuilder.DropColumn(
                name: "RecoveryBlobId",
                table: "DavItems");
        }
    }
}
