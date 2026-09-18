"""Verify the production recursive size SQL against deletion scenarios.

Runs without .NET; covers SQLite behavior, not EF integration or the UI.
"""
import re
import sqlite3
import unittest
from pathlib import Path

SOURCE = (Path(__file__).resolve().parents[1] / "backend/Database/DavDatabaseClient.cs").read_text()
SQL = re.search(r'const string sql = @"(.*?)";', SOURCE, re.S).group(1)
CONTENT = "00000000-0000-0000-0000-000000000002"


class LibrarySizeTests(unittest.TestCase):
    def setUp(self):
        self.db = sqlite3.connect(":memory:")
        self.db.execute("CREATE TABLE DavItems (Id TEXT PRIMARY KEY, ParentId TEXT, FileSize INTEGER)")
        self.db.executemany("INSERT INTO DavItems VALUES (?, ?, ?)", [
            (CONTENT, "root", None),
            ("category", CONTENT, None),
            ("release", "category", None),
            ("nested", "release", None),
            ("video", "nested", 5000000000),
            ("subtitle", "release", 1000),
            ("other", "category", 2000),
            ("virtual", "symlinks", 5000000000),
            ("orphan", "missing-parent", 9000),
        ])

    def tearDown(self):
        self.db.close()

    def size(self):
        return self.db.execute(SQL, {"parentId": CONTENT}).fetchone()[0]

    def test_counts_nested_content_once_with_large_sizes(self):
        self.assertEqual(self.size(), 5000003000)

    def test_file_deletion_reduces_size(self):
        self.db.execute("DELETE FROM DavItems WHERE Id = 'video'")
        self.assertEqual(self.size(), 3000)

    def test_folder_deletion_excludes_pending_cleanup_descendants(self):
        self.db.execute("DELETE FROM DavItems WHERE Id = 'release'")
        self.assertEqual(self.size(), 2000)
        self.assertIsNotNone(self.db.execute("SELECT Id FROM DavItems WHERE Id = 'video'").fetchone())
        self.db.execute("DELETE FROM DavItems WHERE Id IN ('nested', 'video', 'subtitle')")
        self.assertEqual(self.size(), 2000)

    def test_empty_library_is_zero(self):
        self.db.execute("DELETE FROM DavItems WHERE Id = 'category'")
        self.assertEqual(self.size(), 0)

    def test_library_is_not_limited_to_recent_history(self):
        self.db.executemany("INSERT INTO DavItems VALUES (?, ?, ?)",
                            [(f"file-{i}", "category", 10) for i in range(150)])
        self.assertEqual(self.size(), 5000004500)

    def test_history_cleanup_does_not_change_library_size(self):
        self.db.execute("CREATE TABLE HistoryItems (Id TEXT, TotalSegmentBytes INTEGER)")
        self.db.execute("INSERT INTO HistoryItems VALUES ('job', 5000003000)")
        before = self.size()
        self.db.execute("DELETE FROM HistoryItems")
        self.assertEqual(self.size(), before)


if __name__ == "__main__":
    unittest.main()
