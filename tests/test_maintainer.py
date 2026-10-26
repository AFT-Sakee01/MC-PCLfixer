"""Offline boundary tests for the maintainer; no network, CNG, or git writes."""

import copy
import base64
from contextlib import ExitStack
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock
from datetime import datetime, timedelta, timezone
import zipfile


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "maintain-recipes.py"
SPEC = importlib.util.spec_from_file_location("recipe_maintainer_under_test", SCRIPT)
maintainer = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = maintainer
SPEC.loader.exec_module(maintainer)

NOW = datetime(2026, 10, 21, 0, 0, tzinfo=timezone.utc)
LAUNCHER = "Plain Craft Launcher 2.exe"
HEAD = "a" * 40
COMMIT = "b" * 40
EXECUTABLE = b"MZ" + b"test-only-executable" * 80


def release():
    return {
        "id": 12345,
        "tag_name": "2.99.0.0",
        "draft": False,
        "prerelease": False,
        "published_at": "2026-10-20T00:00:00Z",
        "updated_at": "2026-10-20T01:00:00Z",
        "html_url": "https://github.com/Meloong-Git/PCL/releases/tag/2.99.0.0",
        "assets": [],
    }


def utc(value):
    return value.isoformat().replace("+00:00", "Z")


def profile(identifier):
    return {
        "download": {
            "schema_version": 1,
            "restricted_sha256": ("%064x" % identifier),
            "methods": [{"name": "test_only_" + str(identifier)}],
        },
        "update": {"schema_version": 1, "before_hash": ("%064x" % identifier)},
    }


def catalog(profiles=None, expires=None):
    return {
        "schema_version": 1,
        "sequence": 10,
        "issued_utc": utc(NOW - timedelta(days=1)),
        "expires_utc": utc(expires or NOW + timedelta(days=30)),
        "min_tool_version": "1.0.0.2",
        "profiles": copy.deepcopy(profiles if profiles is not None else [profile(1)]),
    }


class GitDouble:
    """A read/commit/push protocol double; it never invokes a real git process."""

    def __init__(self, remote=HEAD, unstaged=None, staged=None, untracked=None, fail_push=False):
        self.remote = remote
        self.unstaged = list(unstaged if unstaged is not None else maintainer.PUBLISH_PATHS)
        self.staged = list(staged or [])
        self.untracked = list(untracked or [])
        self.fail_push = fail_push
        self.calls = []

    def __call__(self, args, cwd=None, timeout=180):
        args = list(map(str, args))
        self.calls.append((args, cwd, timeout))
        if args[:2] == ["git", "ls-remote"]:
            return self.remote + "\trefs/heads/main"
        if args[:2] == ["git", "diff"]:
            return "\n".join(self.staged if "--cached" in args or "--staged" in args else self.unstaged)
        if args[:2] == ["git", "ls-files"]:
            return "\n".join(self.untracked)
        if args[:2] == ["git", "add"]:
            self.staged += args[3:]
            return ""
        if args[0] == "git" and "commit" in args:
            return "Created test commit"
        if args[:2] == ["git", "push"]:
            if self.fail_push:
                raise maintainer.Refused("Simulated non-fast-forward rejection")
            return ""
        if args[:3] == ["git", "rev-parse", "HEAD"]:
            return COMMIT
        raise AssertionError("Unexpected git test protocol: " + repr(args))

    def operations(self, name):
        return [args for args, _, _ in self.calls if name in args]


class SyncGitDouble:
    def __init__(self, local=HEAD, fetched=COMMIT, dirty=False, changes=None, diverged=False):
        self.local = local
        self.fetched = fetched
        self.dirty = dirty
        self.changes = list(changes if changes is not None else maintainer.PUBLISH_PATHS)
        self.diverged = diverged
        self.calls = []

    def __call__(self, args, cwd=None, timeout=180):
        args = list(map(str, args))
        self.calls.append(args)
        if args[:2] == ["git", "status"]:
            return " M user-test-edit.txt" if self.dirty else ""
        if args[:3] == ["git", "rev-parse", "HEAD"]:
            return self.local
        if args[:3] == ["git", "rev-parse", "FETCH_HEAD"]:
            return self.fetched
        if args[:2] == ["git", "fetch"]:
            return ""
        if args[:2] == ["git", "diff"]:
            return "\n".join(self.changes)
        if args[:2] == ["git", "merge"]:
            if self.diverged:
                raise maintainer.Refused("Simulated non-fast-forward merge refusal")
            self.local = args[-1]
            return ""
        raise AssertionError("Unexpected synchronization test operation: " + repr(args))


class PipelineDouble:
    """Create isolated public fixtures while mocking every external operation."""

    def __init__(self, root, work):
        self.root = root
        self.run_dir = work / "pipeline"
        self.run_dir.mkdir()
        (root / ".dotnet").mkdir()
        (root / ".dotnet/dotnet.exe").write_bytes(b"not executable; mocked")
        (root / "src").mkdir()
        (root / "src/Program.cs").write_text('public const string Version="1.0.0.3";', encoding="utf-8")
        self.release = release()
        self.catalog = catalog()
        self.profile = profile(2)
        self.old_state = None
        self.evidence_changes = {}
        self.failure = None
        self.dirty = False
        self.local_head = HEAD
        self.sync_changes = ["src/Program.cs"]
        self.calls = []
        self.downloads = 0
        self.signed = b"test-only-signed-feed"
        self.publish = mock.Mock(side_effect=self.publish_feed)

    def publish_feed(self, checkout, expected_head):
        if len(self.tool_calls("verify")) != 1:
            raise AssertionError("Publication was attempted before signed feed verification")
        return COMMIT

    def download(self, release_value, output):
        self.downloads += 1
        with zipfile.ZipFile(output, "w") as archive:
            archive.writestr(LAUNCHER, EXECUTABLE)

    def command(self, args, cwd=None, timeout=180):
        args = list(map(str, args))
        self.calls.append(args)
        if args[:2] == ["git", "clone"]:
            checkout = Path(args[-1])
            (checkout / "feed").mkdir(parents=True)
            (checkout / "feed/stable.json").write_bytes(b"test-only-remote-baseline")
            (checkout / "Docs/Maintenance").mkdir(parents=True)
            (checkout / "Docs/Maintenance/CHANGELOG.jsonl").write_text("", encoding="utf-8")
            if self.old_state is not None:
                (checkout / "feed/maintenance-state.json").write_text(json.dumps(self.old_state), encoding="utf-8")
            return ""
        if args[:3] == ["git", "rev-parse", "HEAD"]:
            return HEAD if cwd is not None else self.local_head
        if args[:3] == ["git", "rev-parse", "FETCH_HEAD"]:
            return HEAD
        if args[:2] == ["git", "status"]:
            return " M test-only-dirty.txt" if self.dirty else ""
        if args[:2] == ["git", "fetch"]:
            return ""
        if args[:2] == ["git", "diff"]:
            return "\n".join(self.sync_changes)
        if args[:2] == ["git", "merge"]:
            self.local_head = args[-1]
            return ""
        if args[:2] == ["gh", "api"]:
            if "/commits?path=" in args[2]:
                return json.dumps([{"sha": COMMIT, "commit": {"message": "2.14.0.0 正式版", "committer": {"date": "2026-10-19T00:00:00Z"}}}])
            if args[2].endswith("/releases/latest"):
                return json.dumps(self.release)
            if "/contents/feed/stable.json?ref=" in args[2]:
                return json.dumps({"content": base64.b64encode(self.signed).decode("ascii")})
        if "--project" in args:
            parts = args[args.index("--") + 1:]
            action = parts[0]
            if self.failure == action:
                raise maintainer.Refused("Simulated " + action + " refusal")
            if action == "catalog":
                Path(parts[-1]).write_text(json.dumps(self.catalog), encoding="utf-8")
                return ""
            if action == "prepare":
                output = Path(parts[-1])
                output.mkdir()
                profile_path = output / "profile.json"
                profile_path.write_text(json.dumps(self.profile), encoding="utf-8")
                patched_path = output / "patched-test.exe"
                patched_path.write_bytes(b"MZtest-only-patched-executable")
                evidence = {
                    "schema_version": 1,
                    "launcher_version": self.release["tag_name"],
                    "target_sha256": maintainer.digest(Path(parts[1])),
                    "profile_sha256": maintainer.digest(profile_path),
                    "patched_sha256": maintainer.digest(patched_path),
                    "checks_passed": len(maintainer.REQUIRED_CHECKS),
                    "check_names": sorted(maintainer.REQUIRED_CHECKS),
                    "mode": "reviewed_exact",
                }
                evidence.update(self.evidence_changes)
                (output / "evidence.json").write_text(json.dumps(evidence), encoding="utf-8")
                return ""
            if action == "sign":
                Path(parts[-1]).write_bytes(self.signed)
                return ""
            if action == "verify":
                return "Verified"
        if args[0] == "powershell":
            if self.failure == "gates":
                raise maintainer.Refused("Simulated regression gate failure")
            return ""
        if args[0] == sys.executable:
            return "PASS"
        raise AssertionError("Unexpected pipeline test operation: " + repr(args))

    def patches(self):
        stack = ExitStack()
        original_safe_path = maintainer.safe_work_path
        original_sync = maintainer.sync_publication_baseline
        stack.enter_context(mock.patch.object(maintainer, "ROOT", self.root))
        stack.enter_context(mock.patch.object(maintainer, "command", self.command))
        stack.enter_context(mock.patch.object(maintainer, "download_official", self.download))
        stack.enter_context(mock.patch.object(maintainer, "publish_checkout", self.publish))
        stack.enter_context(mock.patch.object(
            maintainer, "sync_publication_baseline",
            side_effect=lambda expected: original_sync(expected, run=self.command),
        ))
        stack.enter_context(mock.patch.object(
            maintainer, "safe_work_path",
            side_effect=lambda path, root=None: original_safe_path(path, self.root if root is None else root),
        ))
        return stack

    def run(self, mode="run", publish=True, force_refresh=False):
        with self.patches():
            return maintainer.pipeline(SimpleNamespace(
                command=mode, publish=publish, force_refresh=force_refresh,
            ), self.run_dir)

    def tool_calls(self, action):
        return [args for args in self.calls if "--" in args and args[args.index("--") + 1] == action]


class MaintainerTests(unittest.TestCase):
    def test_archive_commit_ahead_of_release(self):
        old = maintainer.parse_release(release())
        commits = [{"sha": COMMIT, "commit": {"message": "2.99.0.1 正式版", "committer": {"date": "2026-10-24T16:45:40Z"}}}]
        selected = maintainer.select_distribution(old, commits)
        self.assertEqual(selected["tag"], "2.99.0.1")
        self.assertEqual(selected["source_commit"], COMMIT)
        commits[0]["commit"]["message"] = "2.99.0.0 正式版"
        self.assertIn("source_commit", maintainer.select_distribution(old, commits))
        commits[0]["commit"]["message"] = "2.14.0.0 正式版"
        self.assertEqual(maintainer.select_distribution(old, commits), old)

    def test_ambiguous_archive_commit_refused(self):
        for value in ([], [{"sha": COMMIT, "commit": {"message": "unreviewed rename"}}], [{"sha": "main", "commit": {"message": "2.99.0.1 正式版"}}]):
            with self.assertRaises(maintainer.Refused):
                maintainer.select_distribution(maintainer.parse_release(release()), value)

    def test_schema_two_requires_new_helper_without_dropping_old(self):
        newer = profile(2)
        newer["download"]["schema_version"] = 2
        merged = maintainer.merge_profile(catalog(), newer, NOW)
        self.assertEqual(merged["min_tool_version"], "1.0.0.4")
        self.assertEqual(len(merged["profiles"]), 2)
        merged["min_tool_version"] = "1.0.0.5"
        self.assertEqual(maintainer.merge_profile(merged, newer, NOW)["min_tool_version"], "1.0.0.5")

    def setUp(self):
        # Every filesystem fixture belongs to a disposable root. In particular,
        # no test uses the real archive, Backups, checkout, signing key or feed.
        self.temporary = tempfile.TemporaryDirectory(prefix="pcl-maintainer-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.work = self.root / "Work"
        self.work.mkdir()
        self.addCleanup(mock.patch.stopall)
        mock.patch.object(maintainer.subprocess, "run", side_effect=AssertionError("Real subprocess forbidden in tests")).start()
        mock.patch.object(maintainer.urllib.request, "urlopen", side_effect=AssertionError("Real network forbidden in tests")).start()

    def test_parse_official_release_without_mistaking_source_archives_for_binaries(self):
        result = maintainer.parse_release(release())
        self.assertEqual(result["tag"], "2.99.0.0")
        self.assertEqual(result["release_id"], 12345)
        self.assertEqual(result["published_at"], "2026-10-20T00:00:00Z")
        self.assertNotIn("download_url", result)

    def test_parse_release_rejects_untrusted_or_unstable_metadata(self):
        mutations = (
            ("draft", True), ("prerelease", True), ("id", 0), ("id", True),
            ("tag_name", "../../main"), ("tag_name", "2.99.0"),
            ("published_at", "not a date"), ("updated_at", "not a date"),
            ("html_url", "https://example.test/Meloong-Git/PCL/releases/tag/2.99.0.0"),
            ("html_url", "https://github.com/SomeoneElse/PCL/releases/tag/2.99.0.0"),
        )
        for field, value in mutations:
            with self.subTest(field=field, value=value):
                data = release()
                data[field] = value
                with self.assertRaises((maintainer.Refused, ValueError)):
                    maintainer.parse_release(data)

    def test_refresh_boundary_and_expired_catalog(self):
        self.assertFalse(maintainer.needs_refresh(catalog(expires=NOW + timedelta(days=7, seconds=1)), NOW))
        self.assertTrue(maintainer.needs_refresh(catalog(expires=NOW + timedelta(days=7)), NOW))
        self.assertTrue(maintainer.needs_refresh(catalog(expires=NOW - timedelta(seconds=1)), NOW))

    def test_refresh_invalid_timestamp_is_not_silently_renewed(self):
        value = catalog()
        value["expires_utc"] = "not a timestamp"
        with self.assertRaises((maintainer.Refused, ValueError, zipfile.BadZipFile, FileExistsError)):
            maintainer.needs_refresh(value, NOW)

    def test_merge_increments_sequence_and_preserves_old_profiles(self):
        original = catalog([profile(1), profile(2)])
        before = copy.deepcopy(original)
        addition = profile(3)
        merged = maintainer.merge_profile(original, addition, NOW)
        self.assertEqual(original, before)
        self.assertEqual(merged["sequence"], before["sequence"] + 1)
        self.assertEqual(merged["profiles"], [addition, profile(1), profile(2)])
        self.assertEqual(merged["min_tool_version"], "1.0.0.2")
        self.assertEqual(datetime.fromisoformat(merged["issued_utc"].replace("Z", "+00:00")), NOW)
        self.assertGreater(datetime.fromisoformat(merged["expires_utc"].replace("Z", "+00:00")), NOW)
        merged["profiles"][1]["download"]["methods"][0]["name"] = "changed-test-only"
        self.assertEqual(original, before, "merged catalog must not share mutable original profiles")

    def test_merge_deduplicates_profile_and_keeps_other_supported_versions(self):
        original = catalog([profile(1), profile(2), profile(3)])
        merged = maintainer.merge_profile(original, copy.deepcopy(profile(2)), NOW)
        self.assertEqual(merged["profiles"], [profile(1), profile(2), profile(3)])
        self.assertEqual(original["profiles"], [profile(1), profile(2), profile(3)])

    def test_merge_refuses_to_evict_a_supported_profile_when_full(self):
        original = catalog([profile(i) for i in range(1, 17)])
        before = copy.deepcopy(original)
        with self.assertRaises((maintainer.Refused, ValueError, zipfile.BadZipFile, FileExistsError)):
            maintainer.merge_profile(original, profile(17), NOW)
        self.assertEqual(original, before)
        renewal = maintainer.merge_profile(original, profile(8), NOW)
        self.assertEqual(len(renewal["profiles"]), 16)
        self.assertEqual(renewal["profiles"], original["profiles"])

    def test_safe_work_path_accepts_new_descendants_without_creating_them(self):
        target = self.work / "new-run" / LAUNCHER
        self.assertEqual(maintainer.safe_work_path(target, self.root), target.resolve())
        self.assertFalse(target.exists())
        self.assertFalse(target.parent.exists())

    def test_safe_work_path_refuses_archive_and_protected_directories(self):
        for target in (
            self.root / "PCL2DLA.zip",
            self.root / "Backups" / LAUNCHER,
            self.work / "Backups" / LAUNCHER,
            self.work / ".pcl-repair" / LAUNCHER,
            self.work / ".." / "outside.exe",
        ):
            with self.subTest(target=target.relative_to(self.root)):
                with self.assertRaises((maintainer.Refused, ValueError, zipfile.BadZipFile, FileExistsError)):
                    maintainer.safe_work_path(target, self.root)

    def test_safe_work_path_refuses_symlink_ancestor(self):
        outside = self.root / "outside"
        outside.mkdir()
        link = self.work / "linked"
        try:
            link.symlink_to(outside, target_is_directory=True)
        except OSError as error:
            self.skipTest("Host cannot create the disposable symlink fixture: " + type(error).__name__)
        with self.assertRaises((maintainer.Refused, ValueError, zipfile.BadZipFile, FileExistsError)):
            maintainer.safe_work_path(link / LAUNCHER, self.root)
        self.assertFalse((outside / LAUNCHER).exists())

    def make_archive(self, entries):
        archive = self.work / "download.zip"
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as zipped:
            for name, content in entries:
                zipped.writestr(name, content)
        return archive

    def test_zip_extracts_only_the_launcher_and_never_reads_private_entries(self):
        executable = EXECUTABLE
        archive = self.make_archive([
            ("PCL/" + LAUNCHER, executable),
            ("PCL/Setup.ini", b"test-only-account-setting"),
            ("PCL/Log1.txt", b"test-only-private-log"),
            ("PCL/Launch.bat", b"test-only-script"),
            ("../outside-setting.txt", b"test-only-traversal"),
        ])
        output = self.work / LAUNCHER
        opened = []
        real_open = zipfile.ZipFile.open

        def tracked_open(zipped, member, *args, **kwargs):
            opened.append(member.filename if isinstance(member, zipfile.ZipInfo) else member)
            return real_open(zipped, member, *args, **kwargs)

        with mock.patch.object(zipfile.ZipFile, "open", tracked_open):
            returned = maintainer.extract_launcher(archive, output)
        self.assertEqual(returned, output)
        self.assertEqual(output.read_bytes(), executable)
        self.assertEqual(opened, ["PCL/" + LAUNCHER])
        self.assertFalse((self.root / "outside-setting.txt").exists())
        self.assertEqual(sorted(p.name for p in self.work.iterdir()), [LAUNCHER, "download.zip"])

    def test_zip_requires_exactly_one_launcher(self):
        archive = self.make_archive([
            ("first/" + LAUNCHER, EXECUTABLE),
            ("second/" + LAUNCHER, EXECUTABLE),
        ])
        output = self.work / LAUNCHER
        with self.assertRaises((maintainer.Refused, ValueError, zipfile.BadZipFile, FileExistsError)):
            maintainer.extract_launcher(archive, output)
        self.assertFalse(output.exists())
        self.assertTrue(archive.exists())

    def test_zip_missing_launcher_does_not_create_output(self):
        archive = self.make_archive([("Setup.ini", b"test-only-settings")])
        output = self.work / LAUNCHER
        with self.assertRaises((maintainer.Refused, ValueError, zipfile.BadZipFile, FileExistsError)):
            maintainer.extract_launcher(archive, output)
        self.assertFalse(output.exists())

    def test_zip_existing_output_is_preserved(self):
        archive = self.make_archive([(LAUNCHER, EXECUTABLE)])
        output = self.work / LAUNCHER
        output.write_bytes(b"existing-test-copy")
        with self.assertRaises((maintainer.Refused, ValueError, zipfile.BadZipFile, FileExistsError)):
            maintainer.extract_launcher(archive, output)
        self.assertEqual(output.read_bytes(), b"existing-test-copy")

    def test_zip_oversized_launcher_is_rejected_before_decompression(self):
        archive = self.make_archive([(LAUNCHER, EXECUTABLE)])
        with zipfile.ZipFile(archive) as zipped:
            entry = copy.copy(zipped.infolist()[0])
        entry.file_size = 64 * 1024 * 1024 + 1
        output = self.work / LAUNCHER
        with mock.patch.object(zipfile.ZipFile, "infolist", return_value=[entry]):
            with mock.patch.object(zipfile.ZipFile, "open") as opened:
                with self.assertRaises((maintainer.Refused, ValueError, zipfile.BadZipFile, FileExistsError)):
                    maintainer.extract_launcher(archive, output)
                opened.assert_not_called()
        self.assertFalse(output.exists())

    def test_zip_corrupt_crc_removes_only_its_partial_output(self):
        archive = self.work / "bad-crc.zip"
        payload = EXECUTABLE
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_STORED) as zipped:
            zipped.writestr(LAUNCHER, payload)
        content = bytearray(archive.read_bytes())
        start = content.index(payload)
        content[start + 5] ^= 1
        archive.write_bytes(content)
        output = self.work / LAUNCHER
        with self.assertRaises((maintainer.Refused, ValueError, zipfile.BadZipFile, FileExistsError)):
            maintainer.extract_launcher(archive, output)
        self.assertFalse(output.exists())

    def test_publish_refuses_changed_remote_before_staging_or_committing(self):
        git = GitDouble(remote="c" * 40)
        with self.assertRaises(maintainer.Refused):
            maintainer.publish_checkout(self.work, HEAD, run=git)
        self.assertEqual(git.operations("add"), [])
        self.assertEqual(git.operations("commit"), [])
        self.assertEqual(git.operations("push"), [])

    def test_publish_rejects_unexpected_unstaged_untracked_and_staged_paths(self):
        for area in ("unstaged", "untracked", "staged"):
            with self.subTest(area=area):
                git = GitDouble(**{area: ["unrelated-test-user-file.txt"]})
                with self.assertRaises(maintainer.Refused):
                    maintainer.publish_checkout(self.work, HEAD, run=git)
                self.assertEqual(git.operations("commit"), [])
                self.assertEqual(git.operations("push"), [])

    def test_publish_commits_only_allowlisted_paths_and_never_forces(self):
        git = GitDouble()
        result = maintainer.publish_checkout(self.work, HEAD, run=git)
        self.assertEqual(result, COMMIT)
        self.assertEqual(git.operations("add"), [["git", "add", "--", *maintainer.PUBLISH_PATHS]])
        self.assertEqual(git.operations("push"), [["git", "push", "origin", "HEAD:refs/heads/main"]])
        for args, cwd, _ in git.calls:
            self.assertEqual(cwd, self.work)
            self.assertFalse(any(arg.startswith("--force") for arg in args))
            self.assertNotIn("reset", args)
            self.assertNotIn("clean", args)

    def test_publish_push_race_fails_without_retrying_or_resetting(self):
        git = GitDouble(fail_push=True)
        with self.assertRaises(maintainer.Refused):
            maintainer.publish_checkout(self.work, HEAD, run=git)
        self.assertEqual(len(git.operations("push")), 1)
        self.assertEqual(git.operations("reset"), [])
        self.assertEqual(git.operations("clean"), [])

    def test_sync_unchanged_head_does_not_fetch_or_modify_checkout(self):
        git = SyncGitDouble(local=COMMIT)
        maintainer.sync_publication_baseline(COMMIT, run=git)
        self.assertFalse(any("fetch" in args or "merge" in args for args in git.calls))

    def test_sync_clean_feed_only_history_fast_forwards_for_next_automatic_run(self):
        git = SyncGitDouble()
        maintainer.sync_publication_baseline(COMMIT, run=git)
        self.assertEqual(git.local, COMMIT)
        self.assertIn(["git", "fetch", "--no-tags", "https://github.com/AFT-Sakee01/MC-PCLfixer.git", "main"], git.calls)
        self.assertIn(["git", "merge", "--ff-only", COMMIT], git.calls)
        self.assertFalse(any("reset" in args or "clean" in args for args in git.calls))

    def test_sync_refuses_dirty_checkout_before_fetch(self):
        git = SyncGitDouble(dirty=True)
        with self.assertRaises(maintainer.Refused):
            maintainer.sync_publication_baseline(COMMIT, run=git)
        self.assertFalse(any("fetch" in args or "merge" in args for args in git.calls))

    def test_sync_refuses_remote_maintenance_code_changes(self):
        git = SyncGitDouble(changes=["scripts/maintain-recipes.py"])
        with self.assertRaises(maintainer.Refused):
            maintainer.sync_publication_baseline(COMMIT, run=git)
        self.assertFalse(any("merge" in args for args in git.calls))
        self.assertEqual(git.local, HEAD)

    def test_sync_refuses_remote_race_after_frozen_clone(self):
        git = SyncGitDouble(fetched="c" * 40)
        with self.assertRaises(maintainer.Refused):
            maintainer.sync_publication_baseline(COMMIT, run=git)
        self.assertFalse(any("merge" in args for args in git.calls))

    def test_sync_diverged_history_has_no_force_reset_or_rebase_fallback(self):
        git = SyncGitDouble(diverged=True)
        with self.assertRaises(maintainer.Refused):
            maintainer.sync_publication_baseline(COMMIT, run=git)
        self.assertEqual(git.local, HEAD)
        self.assertEqual(sum("merge" in args for args in git.calls), 1)
        self.assertFalse(any(any(value in args for value in ("reset", "rebase", "--force")) for args in git.calls))

    def test_pipeline_check_does_not_download_prepare_sign_or_publish(self):
        pipeline = PipelineDouble(self.root, self.work)
        result = pipeline.run(mode="check", publish=False)
        self.assertEqual(result["status"], "checked")
        self.assertEqual(pipeline.downloads, 0)
        self.assertEqual(pipeline.tool_calls("prepare"), [])
        self.assertEqual(pipeline.tool_calls("sign"), [])
        pipeline.publish.assert_not_called()

    def test_pipeline_authentication_failure_stops_before_download_and_sign(self):
        pipeline = PipelineDouble(self.root, self.work)
        pipeline.failure = "catalog"
        with self.assertRaises(maintainer.Refused):
            pipeline.run()
        self.assertEqual(pipeline.downloads, 0)
        self.assertEqual(pipeline.tool_calls("sign"), [])
        pipeline.publish.assert_not_called()

    def test_pipeline_prepare_refusal_stops_before_sign_or_publish(self):
        pipeline = PipelineDouble(self.root, self.work)
        pipeline.failure = "prepare"
        with self.assertRaises(maintainer.Refused):
            pipeline.run()
        self.assertEqual(pipeline.tool_calls("sign"), [])
        pipeline.publish.assert_not_called()

    def test_pipeline_candidate_evidence_must_match_release_input_and_profile(self):
        for index, (field, value) in enumerate((
            ("schema_version", 2),
            ("launcher_version", "0.0.0.0"),
            ("target_sha256", "0" * 64),
            ("profile_sha256", "0" * 64),
            ("patched_sha256", "0" * 64),
            ("checks_passed", 0),
            ("check_names", ["unknown_check"]),
            ("check_names", sorted(maintainer.REQUIRED_CHECKS) + [sorted(maintainer.REQUIRED_CHECKS)[0]]),
            ("mode", "unreviewed_guess"),
        )):
            with self.subTest(field=field):
                case_root = self.root / (field + str(index))
                case_root.mkdir()
                case_work = case_root / "Work"
                case_work.mkdir()
                pipeline = PipelineDouble(case_root, case_work)
                pipeline.evidence_changes[field] = value
                with self.assertRaises(maintainer.Refused):
                    pipeline.run()
                self.assertEqual(pipeline.tool_calls("sign"), [])
                pipeline.publish.assert_not_called()

    def test_pipeline_regression_failures_stop_before_sign_or_publish(self):
        pipeline = PipelineDouble(self.root, self.work)
        pipeline.failure = "gates"
        with self.assertRaises(maintainer.Refused):
            pipeline.run()
        self.assertEqual(pipeline.tool_calls("sign"), [])
        pipeline.publish.assert_not_called()

    def test_pipeline_known_release_outside_renewal_window_is_noop(self):
        pipeline = PipelineDouble(self.root, self.work)
        pipeline.profile = profile(1)
        pipeline.catalog = catalog(expires=datetime.now(timezone.utc) + timedelta(days=30))
        pipeline.old_state = {
            "release": maintainer.parse_release(pipeline.release),
            "target_sha256": hashlib.sha256(EXECUTABLE).hexdigest(),
        }
        result = pipeline.run()
        self.assertEqual(result["status"], "unchanged")
        self.assertEqual(pipeline.tool_calls("sign"), [])
        self.assertFalse(any(args[0] == "powershell" for args in pipeline.calls))
        pipeline.publish.assert_not_called()

    def test_pipeline_validation_mode_runs_gates_but_does_not_sign(self):
        pipeline = PipelineDouble(self.root, self.work)
        result = pipeline.run(publish=False)
        self.assertEqual(result["status"], "validated")
        self.assertTrue(any(args[0] == "powershell" for args in pipeline.calls))
        self.assertEqual(pipeline.tool_calls("sign"), [])
        pipeline.publish.assert_not_called()

    def test_pipeline_dirty_maintainer_cannot_sign_or_publish(self):
        pipeline = PipelineDouble(self.root, self.work)
        pipeline.dirty = True
        with self.assertRaises(maintainer.Refused):
            pipeline.run()
        self.assertEqual(pipeline.tool_calls("sign"), [])
        pipeline.publish.assert_not_called()

    def test_pipeline_maintainer_code_must_match_validated_remote_commit(self):
        pipeline = PipelineDouble(self.root, self.work)
        pipeline.local_head = "c" * 40
        with self.assertRaises(maintainer.Refused):
            pipeline.run()
        self.assertEqual(pipeline.tool_calls("sign"), [])
        pipeline.publish.assert_not_called()

    def test_pipeline_can_publish_again_after_its_previous_feed_only_commit(self):
        pipeline = PipelineDouble(self.root, self.work)
        pipeline.local_head = "c" * 40
        pipeline.sync_changes = list(maintainer.PUBLISH_PATHS)
        result = pipeline.run()
        self.assertEqual(result["status"], "published")
        self.assertEqual(pipeline.local_head, HEAD)
        self.assertTrue(any(args[:3] == ["git", "merge", "--ff-only"] for args in pipeline.calls))
        pipeline.publish.assert_called_once()

    def test_pipeline_signing_or_signature_verification_failure_prevents_publication(self):
        for action in ("sign", "verify"):
            with self.subTest(action=action):
                case_root = self.root / action
                case_root.mkdir()
                case_work = case_root / "Work"
                case_work.mkdir()
                pipeline = PipelineDouble(case_root, case_work)
                pipeline.failure = action
                with self.assertRaises(maintainer.Refused):
                    pipeline.run()
                pipeline.publish.assert_not_called()

    def test_pipeline_signs_only_after_required_gate_and_verifies_before_publish(self):
        pipeline = PipelineDouble(self.root, self.work)
        result = pipeline.run()
        self.assertEqual(result["status"], "published")
        self.assertEqual(result["commit"], COMMIT)
        sign = pipeline.tool_calls("sign")
        verify = pipeline.tool_calls("verify")
        self.assertEqual(len(sign), 1)
        self.assertEqual(len(verify), 1)
        sign_index = pipeline.calls.index(sign[0])
        self.assertTrue(any(args[0] == "powershell" for args in pipeline.calls[:sign_index]))
        self.assertGreater(pipeline.calls.index(verify[0]), sign_index)
        pipeline.publish.assert_called_once_with(pipeline.run_dir / "publication", HEAD)

    def test_zip_truncated_archive_does_not_create_output(self):
        archive = self.work / "broken.zip"
        archive.write_bytes(b"PK\x03\x04truncated-test-fixture")
        output = self.work / LAUNCHER
        with self.assertRaises((maintainer.Refused, ValueError, zipfile.BadZipFile, FileExistsError)):
            maintainer.extract_launcher(archive, output)
        self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
