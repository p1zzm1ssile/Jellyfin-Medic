"""Tests for scripts/github-downloads.py: python3 -m unittest discover -s scripts/tests"""
import csv
import importlib.util
import pathlib
import sys
import tempfile
import unittest
from datetime import datetime, timezone

SCRIPTS = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))

_spec = importlib.util.spec_from_file_location("github_downloads", SCRIPTS / "github-downloads.py")
gd = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(gd)


def utc(text):
    return datetime.strptime(text, gd.TIME_FORMAT).replace(tzinfo=timezone.utc)


def write_csv(rows):
    f = tempfile.NamedTemporaryFile("w", suffix=".csv", delete=False, newline="")
    w = csv.writer(f)
    w.writerow(["Timestamp", "Kind", "Name", "Downloads"])
    w.writerows(rows)
    f.close()
    return f.name


# Winter (GMT): UK time and UTC are the same.
WINTER = [
    ["2026-11-01 00:40:00", "image", "dubarr", 2600],
    ["2026-11-01 00:40:00", "release", "Jellyfin Medic", 50],
    ["2026-11-01 18:30:00", "image", "dubarr", 2680],
    ["2026-11-01 18:30:00", "release", "Jellyfin Medic", 51],
    ["2026-11-02 00:35:00", "image", "dubarr", 2700],
    ["2026-11-02 00:35:00", "release", "Jellyfin Medic", 52],
    ["2026-11-02 06:20:00", "image", "dubarr", 2710],
    ["2026-11-02 06:20:00", "release", "Jellyfin Medic", 52],
]


class MessageTests(unittest.TestCase):
    def test_lines_show_change_since_last_run_and_total_since_midnight(self):
        hist = gd.history(write_csv(WINTER))
        counts = [("image", "dubarr", 2727), ("release", "Jellyfin Medic", 53)]
        msg = gd.build_message(counts, [], hist, utc("2026-11-02 12:15:00"))
        self.assertIn("Changes since the last run at 06:20", msg)
        self.assertIn("• dubarr: 2,727 (+17)", msg)
        self.assertIn("• Jellyfin Medic: 53 (+1)", msg)
        # Since the first run after midnight (00:35): 2700 + 52 -> 2727 + 53
        self.assertIn("Total: 2,780 (+28 today since 00:00)", msg)
        self.assertNotIn("24h", msg)

    def test_first_run_after_midnight_sends_the_whole_day_before(self):
        rows = WINTER[:4]  # nothing logged yet on 2 Nov
        hist = gd.history(write_csv(rows))
        counts = [("image", "dubarr", 2700), ("release", "Jellyfin Medic", 52)]
        msg = gd.build_message(counts, [], hist, utc("2026-11-02 00:47:00"))
        self.assertIn("Changes since the last run at Sun 01 Nov 18:30", msg)
        # From the first run on 1 Nov (2600 + 50) to now (2700 + 52)
        self.assertIn("Total: 2,752 (+102 for the whole of Sun 01 Nov)", msg)

    def test_a_late_midnight_run_still_closes_the_day_before(self):
        hist = gd.history(write_csv(WINTER[:4]))
        counts = [("image", "dubarr", 2705), ("release", "Jellyfin Medic", 52)]
        msg = gd.build_message(counts, [], hist, utc("2026-11-02 03:40:00"))
        self.assertIn("for the whole of Sun 01 Nov", msg)

    def test_days_follow_uk_time_in_summer(self):
        # 23:10 UTC on 30 Jun is 00:10 BST on 1 Jul, so this is the first run of 1 Jul.
        rows = [
            ["2026-06-29 23:05:00", "image", "dubarr", 100],  # 00:05 BST 30 Jun
            ["2026-06-30 17:05:00", "image", "dubarr", 150],  # 18:05 BST 30 Jun
        ]
        hist = gd.history(write_csv(rows))
        msg = gd.build_message([("image", "dubarr", 160)], [], hist, utc("2026-06-30 23:10:00"))
        self.assertIn("📊 GitHub downloads, Wed 01 Jul 2026 00:10", msg)
        self.assertIn("Total: 160 (+60 for the whole of Tue 30 Jun)", msg)

    def test_a_new_line_is_marked_and_adds_nothing_to_the_day(self):
        hist = gd.history(write_csv(WINTER))
        counts = [("image", "dubarr", 2710), ("image", "evictarr", 900), ("release", "Jellyfin Medic", 52)]
        msg = gd.build_message(counts, [], hist, utc("2026-11-02 12:15:00"))
        self.assertIn("• evictarr: 900 (new)", msg)
        self.assertIn("(+10 today since 00:00)", msg)

    def test_no_history_shows_just_the_total(self):
        msg = gd.build_message([("image", "dubarr", 5)], [], {}, utc("2026-11-02 12:15:00"))
        self.assertIn("Total: 5", msg)
        self.assertNotIn("(+", msg)
        self.assertNotIn("last run", msg)

    def test_errors_are_listed(self):
        msg = gd.build_message([("image", "dubarr", 5)], ["image foo: boom"], {}, utc("2026-11-02 12:15:00"))
        self.assertIn("⚠️ Couldn't read:\n• image foo: boom", msg)


class DayStartTests(unittest.TestCase):
    def test_falls_back_to_the_last_run_before_a_day_with_no_runs(self):
        hist = gd.history(write_csv([
            ["2026-10-30 18:00:00", "image", "dubarr", 10],
            ["2026-11-01 09:00:00", "image", "dubarr", 30],
        ]))
        day = datetime(2026, 10, 31).date()
        self.assertEqual(gd.day_start(hist[("image", "dubarr")], day), 10)

    def test_none_when_the_line_is_newer_than_the_day(self):
        hist = gd.history(write_csv([["2026-11-02 09:00:00", "image", "dubarr", 30]]))
        self.assertIsNone(gd.day_start(hist[("image", "dubarr")], datetime(2026, 11, 1).date()))


if __name__ == "__main__":
    unittest.main()
