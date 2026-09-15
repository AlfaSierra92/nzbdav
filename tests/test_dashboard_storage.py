"""Exercise the actual dashboard SQL without requiring the .NET runtime.

This verifies the storage contract, not C#/EF integration or the React UI.
Run: python3 -m unittest discover -s tests -v
"""
import re
import sqlite3
import tempfile
import unittest
from pathlib import Path

SOURCE = (Path(__file__).resolve().parents[1] / "backend/Statistics/DashboardStatisticsStore.cs").read_text()
SCHEMA = re.search(r'command.CommandText = """(.*?)""";', SOURCE, re.S).group(1)
QUERIES = re.findall(r'await Read\("""(.*?)""", reader', SOURCE, re.S)
INSERTS = re.findall(r'await Execute\("([^"]+)"', SOURCE)


class DashboardStorageTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.path = Path(self.directory.name) / "statistics-v1.sqlite"
        self.db = sqlite3.connect(self.path)
        self.db.executescript(SCHEMA)

    def tearDown(self):
        self.db.close()
        self.directory.cleanup()

    def sample(self, time=60, active=4, queue=2):
        self.db.execute(INSERTS[0], dict(time=time, active=active, idle=1, capacity=10, queue=queue))

    def job(self, id="one", time=60, completed=1, size=1024):
        self.db.execute(INSERTS[1], dict(id=id, time=time, completed=completed, bytes=size))

    def check(self, id="one", time=60, healthy=1):
        self.db.execute(INSERTS[2], dict(id=id, time=time, healthy=healthy))

    def read(self, query, start=0, end=86400, step=3600):
        return self.db.execute(QUERIES[query], {"from": start, "to": end, "step": step}).fetchall()

    def test_persists_after_reopen_and_does_not_modify_main_database(self):
        main = Path(self.directory.name) / "db.sqlite"
        main.write_bytes(b"existing application database")
        self.sample()
        self.job()
        self.check()
        self.db.execute(INSERTS[3], {"time": 60})
        self.db.commit()
        self.db.close()
        self.db = sqlite3.connect(self.path)
        self.assertEqual(self.read(0)[0], (0, 1, 4.0, 4, 2.0, 1))
        self.assertEqual(self.read(1)[0], (0, 1, 0, 1024))
        self.assertEqual(self.read(2)[0], (0, 1, 1))
        self.assertEqual(self.db.execute("SELECT value FROM metadata").fetchone(), (60,))
        self.assertEqual(main.read_bytes(), b"existing application database")

    def test_replayed_imports_and_checks_are_not_counted_twice(self):
        for _ in range(3):
            self.sample()
            self.job()
            self.check()
        self.assertEqual(self.read(0)[0][1], 1)
        self.assertEqual(self.read(1), [(0, 1, 0, 1024)])
        self.assertEqual(self.read(2), [(0, 1, 1)])

    def test_failed_write_rolls_back_sample_events_and_checkpoint(self):
        self.sample()
        self.job()
        self.check()
        self.db.execute(INSERTS[3], {"time": 60})
        self.db.rollback()
        for table in ("samples", "imports", "health", "metadata"):
            self.assertEqual(self.db.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0], 0)

    def test_day_week_month_buckets_use_exclusive_end_and_keep_gaps(self):
        for time in (0, 3600, 86400, 7 * 86400, 31 * 86400):
            self.job(str(time), time)
        self.assertEqual(len(self.read(1, end=86400)), 2)
        week = self.read(1, end=7 * 86400, step=86400)
        self.assertEqual(week, [(0, 2, 0, 2048), (1, 1, 0, 1024)])
        month = self.read(1, end=31 * 86400, step=86400)
        self.assertEqual([row[0] for row in month], [0, 1, 7])

    def test_unknown_connections_are_distinct_from_zero(self):
        self.sample(60, active=None)
        self.sample(120, active=0, queue=4)
        self.assertEqual(self.read(0), [(0, 2, 0.0, 0, 3.0, 1)])
        self.assertEqual(self.read(0, start=86400, end=172800), [])

    def test_fifteen_samples_share_one_commit_without_losing_resolution(self):
        commits = []
        self.db.set_trace_callback(lambda statement: commits.append(statement) if statement == "COMMIT" else None)
        for minute in range(15):
            self.sample(time=minute * 60, active=minute)
        self.db.execute(INSERTS[3], {"time": 14 * 60})
        self.db.commit()
        self.assertEqual(len(commits), 1)
        self.assertEqual(self.read(0), [(0, 15, 7.0, 14, 2.0, 15)])

    def test_second_resolution_survives_a_fifteen_minute_batch(self):
        for second in range(900):
            self.sample(time=second, active=second % 60)
        self.db.execute(INSERTS[3], {"time": 899})
        self.db.commit()
        self.assertEqual(self.read(0), [(0, 900, 29.5, 59, 2.0, 900)])
        self.assertEqual(self.db.execute("SELECT COUNT(*) FROM samples WHERE time < 60").fetchone()[0], 60)

    def test_replayed_saved_minute_and_checkpoint_do_not_write(self):
        self.sample(active=4)
        self.db.execute(INSERTS[3], {"time": 60})
        self.db.commit()
        previous_changes = self.db.total_changes
        self.sample(active=99)
        self.db.execute(INSERTS[3], {"time": 60})
        self.db.commit()
        self.assertEqual(self.db.total_changes, previous_changes)
        self.assertEqual(self.read(0)[0][2], 4)

    def test_failed_content_is_excluded_and_health_outcomes_are_counted(self):
        self.job()
        self.job("failed", completed=0, size=999999)
        self.check()
        self.check("bad", healthy=0)
        self.assertEqual(self.read(1), [(0, 1, 1, 1024)])
        self.assertEqual(self.read(2), [(0, 2, 1)])


if __name__ == "__main__":
    unittest.main()
