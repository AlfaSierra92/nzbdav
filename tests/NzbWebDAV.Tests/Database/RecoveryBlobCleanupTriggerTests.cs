using Microsoft.Data.Sqlite;
using NzbWebDAV.Database.Migrations;

namespace NzbWebDAV.Tests.Database;

/// <summary>
/// Deleting a repaired DavItem must schedule its recovery blob for cleanup, the same
/// way NZB blobs are handled: an AFTER DELETE trigger inserts the blob id into
/// BlobCleanupItems for BlobCleanupService to collect.
/// </summary>
public class RecoveryBlobCleanupTriggerTests
{
    private static SqliteConnection OpenDatabase()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        Exec(conn,
            """
            CREATE TABLE DavItems (Id TEXT PRIMARY KEY, RecoveryBlobId TEXT NULL);
            CREATE TABLE BlobCleanupItems (Id TEXT PRIMARY KEY);
            """);
        Exec(conn, AddRecoveryBlobIdToDavItemsTable.CreateTriggerSql);
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Count(SqliteConnection conn, string table, string where = "1=1")
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {where}";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void DeletingRepairedDavItem_SchedulesRecoveryBlobCleanup()
    {
        using var conn = OpenDatabase();
        Exec(conn, "INSERT INTO DavItems VALUES ('item-1', 'blob-1')");

        Exec(conn, "DELETE FROM DavItems WHERE Id = 'item-1'");

        Assert.Equal(1, Count(conn, "BlobCleanupItems", "Id = 'blob-1'"));
    }

    [Fact]
    public void DeletingUnrepairedDavItem_SchedulesNothing()
    {
        using var conn = OpenDatabase();
        Exec(conn, "INSERT INTO DavItems VALUES ('item-2', NULL)");

        Exec(conn, "DELETE FROM DavItems WHERE Id = 'item-2'");

        Assert.Equal(0, Count(conn, "BlobCleanupItems"));
    }
}
