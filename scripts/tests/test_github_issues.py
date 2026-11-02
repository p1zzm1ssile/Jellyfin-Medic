"""Tests for scripts/github-issues.py: python3 -m unittest discover -s scripts/tests"""
import importlib.util
import pathlib
import sys
import unittest

SCRIPTS = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))

_spec = importlib.util.spec_from_file_location("github_issues", SCRIPTS / "github-issues.py")
gi = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(gi)


def issue(id, login, number=1, title="Crash on start", repo="Dubarr"):
    return {"id": id, "number": number, "title": title, "user": {"login": login},
            "repository_url": f"https://api.github.com/repos/P1zzm1ssile/{repo}",
            "html_url": f"https://github.com/P1zzm1ssile/{repo}/issues/{number}",
            "created_at": "2026-11-02T14:00:00Z"}


class IssueTests(unittest.TestCase):
    def test_sends_only_new_issues_from_other_people(self):
        issues = [issue(1, "someone"), issue(2, "someone"), issue(3, "p1zzm1ssile")]
        sent = gi.to_send(issues, {"1": "2026-11-02T13:00:00Z"}, "P1zzm1ssile")
        self.assertEqual([i["id"] for i in sent], [2])

    def test_message_names_the_repo_issue_and_author(self):
        text = gi.message(issue(7, "someone", number=12, repo="Evictarr"))
        self.assertEqual(text, "🐛 New issue in Evictarr\n#12 Crash on start\nby someone\n"
                               "https://github.com/P1zzm1ssile/Evictarr/issues/12")


if __name__ == "__main__":
    unittest.main()
