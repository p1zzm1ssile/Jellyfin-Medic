"""Tests for scripts/release_credits.py: python3 -m unittest discover -s scripts/tests"""
import contextlib
import io
import pathlib
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))

import release_credits as rc  # noqa: E402


class PluginTests(unittest.TestCase):
    def test_prefix_of_each_plugin(self):
        self.assertEqual(rc.plugin_prefix("v1.0.20"), "v")
        self.assertEqual(rc.plugin_prefix("picks-v1.2.4"), "picks-v")
        self.assertEqual(rc.plugin_prefix("profiles-v1.1.3"), "profiles-v")
        with self.assertRaises(ValueError):
            rc.plugin_prefix("nightly")

    def test_removed_plugins_tags_are_refused(self):
        # Medic Posters was released from this repository, then moved out of it.
        with self.assertRaises(ValueError):
            rc.plugin_prefix("film-posters-v1.2.0")

    def test_previous_tag_is_the_same_plugins_last_lower_version(self):
        tags = ["v1.0.9", "v1.0.10", "v1.0.19", "v1.0.20", "picks-v1.2.4", "profiles-v1.1.2", "profiles-v1.1.3", "junk"]
        self.assertEqual(rc.previous_tag("v1.0.20", tags), "v1.0.19")
        self.assertEqual(rc.previous_tag("v1.0.10", tags), "v1.0.9")
        self.assertEqual(rc.previous_tag("profiles-v1.1.3", tags), "profiles-v1.1.2")
        self.assertIsNone(rc.previous_tag("picks-v1.2.4", tags))

    def test_files_belong_to_their_plugin(self):
        self.assertTrue(rc.belongs_to("v", "src/JellyfinMedic/Api/MedicController.cs"))
        self.assertTrue(rc.belongs_to("v", "CHANGELOG.md"))
        self.assertFalse(rc.belongs_to("v", "Jellyfin.Plugin.MedicPicks/Plugin.cs"))
        self.assertFalse(rc.belongs_to("v", "manifest.json"))
        self.assertTrue(rc.belongs_to("picks-v", "Jellyfin.Plugin.MedicPicks/Plugin.cs"))
        self.assertTrue(rc.belongs_to("picks-v", "tests/MedicPicks.Tests/PickTests.cs"))
        self.assertFalse(rc.belongs_to("picks-v", "src/JellyfinMedic/Plugin.cs"))
        self.assertTrue(rc.belongs_to("profiles-v", "Jellyfin.Plugin.MedicProfiles/Plugin.cs"))
        self.assertFalse(rc.belongs_to("profiles-v", "manifest.json"))

    def test_profiles_tests_belong_to_profiles(self):
        self.assertTrue(rc.belongs_to("profiles-v", "tests/MedicProfiles.Tests/ProfileTests.cs"))
        self.assertFalse(rc.belongs_to("v", "tests/MedicProfiles.Tests/ProfileTests.cs"))
        self.assertFalse(rc.belongs_to("picks-v", "tests/MedicProfiles.Tests/ProfileTests.cs"))

    def test_shared_code_belongs_to_picks_and_profiles(self):
        # Both plugins compile shared/Medic.Shared into their DLL, and tests/Shared into their tests.
        for path in ("shared/Medic.Shared/Log.cs", "tests/MedicShared.Tests/LogTests.cs", "tests/Shared/TestSupport.cs"):
            self.assertTrue(rc.belongs_to("picks-v", path), path)
            self.assertTrue(rc.belongs_to("profiles-v", path), path)
            self.assertFalse(rc.belongs_to("v", path), path)


class WindowTests(unittest.TestCase):
    def test_merged_after_the_last_release_and_by_this_one(self):
        self.assertTrue(rc.in_window("2026-10-15T08:45:12Z", "2026-10-14T22:30:00Z", "2026-10-15T15:20:19Z"))
        self.assertFalse(rc.in_window("2026-10-14T21:00:00Z", "2026-10-14T22:30:00Z", "2026-10-15T15:20:19Z"))
        self.assertFalse(rc.in_window("2026-10-15T16:00:00Z", "2026-10-14T22:30:00Z", "2026-10-15T15:20:19Z"))

    def test_first_release_and_release_being_made(self):
        self.assertTrue(rc.in_window("2026-01-01T00:00:00Z", None, "9999"))

    def test_unmerged_pull_request_is_never_in(self):
        self.assertFalse(rc.in_window(None, None, "9999"))


class CreditTests(unittest.TestCase):
    PULLS = [
        {"number": 125, "login": "camarigor", "type": "User"},
        {"number": 117, "login": "camarigor", "type": "User"},
        {"number": 126, "login": "p1zzm1ssile", "type": "User"},
        {"number": 131, "login": "dependabot[bot]", "type": "Bot"},
        {"number": 140, "login": "someone", "type": "User"},
    ]

    def test_owner_and_bots_are_not_thanked(self):
        people = rc.contributors(self.PULLS, "P1zzm1ssile")
        self.assertEqual(people, {"camarigor": {117, 125}, "someone": {140}})

    def test_one_contributor(self):
        line = rc.credit_line({"camarigor": {125, 117, 121}})
        self.assertEqual(line, "Thanks @camarigor (#117, #121, #125) for their work on this release.")

    def test_several_contributors(self):
        line = rc.credit_line({"zed": {3}, "amy": {1}, "Bob": {2}})
        self.assertEqual(line, "Thanks @amy (#1), @Bob (#2) and @zed (#3) for their work on this release.")

    def test_already_credited_in_the_changelog(self):
        changes = "- **A fix.** It works now. Thanks @CamarIgor (#117)."
        self.assertEqual(rc.credit_line({"camarigor": {117}}, changes), "")
        self.assertEqual(rc.credit_line({"camarigor": {117}, "amy": {1}}, changes),
                         "Thanks @amy (#1) for their work on this release.")

    def test_nobody_to_thank(self):
        self.assertEqual(rc.credit_line({}), "")


class FetchTests(unittest.TestCase):
    TAGS = ("v1.0.17", "v1.0.18", "v1.0.19", "v1.0.20")
    # When each release was published, in the tests that give that tag one.
    RELEASES = {"v1.0.17": "2026-10-10T12:00:00Z", "v1.0.18": "2026-10-12T12:00:00Z",
                "v1.0.19": "2026-10-14T23:00:00Z"}
    # Newest first, as the API lists them, all by someone else and all changing Jellyfin Medic.
    MERGED = {141: "2026-10-15T08:45:12Z", 140: "2026-10-13T21:00:00Z", 138: "2026-10-11T08:00:00Z",
              137: "2026-10-09T10:00:00Z"}

    def fetch(self, released, tags=TAGS) -> tuple[list[int], str]:
        """fetch_pulls for v1.0.20 when, of these tags, only the released ones have a release.

        Returns the numbers of the pull requests it keeps and what it printed to stderr.
        """
        def git(*args):
            if args == ("tag", "--list"):
                return "".join(f"{tag}\n" for tag in tags)
            raise AssertionError(f"unexpected git {args}")

        def pages(path):
            if path.endswith("/files"):
                return [{"filename": "src/JellyfinMedic/Plugin.cs"}]
            return [{"number": number, "merged_at": merged_at, "user": {"login": "camarigor", "type": "User"}}
                    for number, merged_at in self.MERGED.items()]

        def published(repo, tag):
            return self.RELEASES[tag] if tag in released else None

        with (mock.patch.multiple(rc, git=git, pages=pages, published=published),
              contextlib.redirect_stderr(io.StringIO()) as stderr):
            pulls = rc.fetch_pulls("v1.0.20", "p1zzm1ssile/Jellyfin-Medic", "p1zzm1ssile")
        return [pr["number"] for pr in pulls], stderr.getvalue()

    def test_last_release_counts_from_when_it_was_published(self):
        self.assertEqual(self.fetch(released={"v1.0.17", "v1.0.18", "v1.0.19"}), ([141], ""))

    def test_tag_without_a_release_counts_from_the_release_before_it(self):
        # Nobody thanked #140 yet: it was merged after v1.0.18's release, and v1.0.19 has no release.
        self.assertEqual(self.fetch(released={"v1.0.17", "v1.0.18"}), (
            [141, 140],
            "No release for v1.0.19: the credits start at v1.0.18's release (2026-10-12T12:00:00Z)\n",
        ))

    def test_tags_in_a_row_without_a_release_count_from_the_release_before_them(self):
        self.assertEqual(self.fetch(released={"v1.0.17"}), (
            [141, 140, 138],
            "No release for v1.0.19, v1.0.18: the credits start at v1.0.17's release (2026-10-10T12:00:00Z)\n",
        ))

    def test_no_earlier_release_counts_everything_merged(self):
        self.assertEqual(self.fetch(released=set()), (
            [141, 140, 138, 137],
            "No release for v1.0.19, v1.0.18, v1.0.17 or earlier: every merged pull request counts\n",
        ))

    def test_first_release_counts_everything_merged(self):
        self.assertEqual(self.fetch(released=set(), tags=("v1.0.20",)), ([141, 140, 138, 137], ""))


if __name__ == "__main__":
    unittest.main()
