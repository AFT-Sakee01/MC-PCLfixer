"""Official upstream retrieval policy tests; all files stay in new Work copies."""

import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
from urllib.parse import quote
import zipfile


ROOT = Path(__file__).resolve().parent.parent
SPEC = importlib.util.spec_from_file_location("official_pcl", ROOT / "scripts/official_pcl.py")
official = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(official)


class OfficialDownloadTests(unittest.TestCase):
    def setUp(self):
        work = ROOT / "Work"
        work.mkdir(exist_ok=True)
        temporary = tempfile.TemporaryDirectory(prefix="official-download-tests-", dir=work)
        self.directory = Path(temporary.name).absolute()
        # TemporaryDirectory's recursive cleanup is scoped to this newly created
        # Work child, never to user references or a path taken from network data.
        self.assertTrue(self.directory.is_relative_to(work.absolute()))
        self.addCleanup(temporary.cleanup)
        self.output = self.directory / "official.zip"
        content = io.BytesIO()
        with zipfile.ZipFile(content, "w") as archive:
            archive.writestr("Plain Craft Launcher 2.exe", b"isolated test fixture")
        self.archive = content.getvalue()
        self.commit = "a" * 40
        self.raw_url = (
            f"https://raw.githubusercontent.com/Meloong-Git/PCL/{self.commit}/"
            + quote(official.ARCHIVE_NAME, safe="")
        )
        self.entry = {
            "type": "file",
            "name": official.ARCHIVE_NAME,
            "path": official.ARCHIVE_NAME,
            "sha": self.blob(self.archive),
            "size": len(self.archive),
            "download_url": self.raw_url,
        }
        self.calls = []

    @staticmethod
    def blob(content):
        return hashlib.sha1(b"blob " + str(len(content)).encode("ascii") + b"\0" + content).hexdigest()

    def read(self, url, limit):
        self.calls.append((url, limit))
        if "/commits/" in url:
            return json.dumps({"sha": self.commit}).encode()
        if "/contents/" in url:
            return json.dumps(self.entry).encode()
        if url == self.raw_url:
            return self.archive
        raise AssertionError("Unexpected external URL")

    def download(self):
        with patch.object(official, "_read", side_effect=self.read):
            return official.download("2.99.0.0", self.output)

    def test_commit_pinned_blob_and_sha256(self):
        result = self.download()
        self.assertEqual(self.output.read_bytes(), self.archive)
        self.assertEqual(result["source_commit"], self.commit)
        self.assertEqual(result["blob_sha"], self.blob(self.archive))
        self.assertEqual(result["zip_sha256"], hashlib.sha256(self.archive).hexdigest())
        self.assertEqual(len(self.calls), 3)
        self.assertEqual(self.calls[-1][0], self.raw_url)
        self.assertIn("?ref=" + self.commit, self.calls[1][0])

    def test_unicode_download_url_from_github_metadata_is_accepted(self):
        self.entry["download_url"] = (
            f"https://raw.githubusercontent.com/Meloong-Git/PCL/{self.commit}/"
            + official.ARCHIVE_NAME
        )
        self.assertEqual(self.download()["size"], len(self.archive))
        self.assertEqual(self.calls[-1][0], self.raw_url)

    def test_invalid_tags_refused_before_network(self):
        for tag in ("../main", "main", "v2.99.0.0", "2.99.0", "2.99.0.0?x", None, "999999.1.1.1"):
            with self.subTest(tag=tag), patch.object(official, "_read") as read:
                with self.assertRaises(official.OfficialDownloadError):
                    official.download(tag, self.output)
                read.assert_not_called()
        self.assertFalse(self.output.exists())

    def test_invalid_commit_identity(self):
        for identity in ("b" * 39, "B" * 40, "../main", None):
            with self.subTest(identity=identity):
                self.commit = identity
                with self.assertRaises(official.OfficialDownloadError):
                    self.download()
                self.assertFalse(self.output.exists())

    def test_metadata_policy_rejects_unexpected_source_type_size_and_digest(self):
        original = copy.deepcopy(self.entry)
        changes = (
            {"type": "symlink"},
            {"name": "other.zip"},
            {"path": "other/最新正式版.zip"},
            {"download_url": "https://example.com/official.zip"},
            {"download_url": self.raw_url.replace("https://", "http://")},
            {"download_url": self.raw_url + "?token=not-accepted"},
            {"download_url": self.raw_url.replace(self.commit, "main")},
            {"size": True},
            {"size": 0},
            {"size": official.MAX_ARCHIVE_BYTES + 1},
            {"sha": "bad"},
            {"sha": "B" * 40},
        )
        for change in changes:
            with self.subTest(change=change):
                self.entry = {**original, **change}
                self.calls.clear()
                with self.assertRaises(official.OfficialDownloadError):
                    self.download()
                self.assertEqual(len(self.calls), 2)
                self.assertFalse(self.output.exists())

    def test_mismatched_content_hash_refused_before_output(self):
        self.archive = self.archive[:-1] + bytes([self.archive[-1] ^ 1])
        with self.assertRaises(official.OfficialDownloadError):
            self.download()
        self.assertFalse(self.output.exists())

    def test_size_mismatch_refused_before_output(self):
        self.entry["size"] += 1
        with self.assertRaises(official.OfficialDownloadError):
            self.download()
        self.assertFalse(self.output.exists())

    def test_non_zip_blob_refused_even_with_matching_git_digest(self):
        self.archive = b"unexpected ordinary file"
        self.entry.update(size=len(self.archive), sha=self.blob(self.archive))
        with self.assertRaises(official.OfficialDownloadError):
            self.download()
        self.assertFalse(self.output.exists())

    def test_existing_output_is_unchanged_without_network(self):
        original = b"existing user file"
        self.output.write_bytes(original)
        with patch.object(official, "_read") as read:
            with self.assertRaises(official.OfficialDownloadError):
                official.download("2.99.0.0", self.output)
            read.assert_not_called()
        self.assertEqual(self.output.read_bytes(), original)

    def test_protected_names_are_refused_without_network(self):
        for name in ("Backups", ".pcl-repair"):
            (self.directory / name).mkdir()
        paths = (
            self.directory / "PCL2DLA.zip",
            self.directory / "Backups" / "official.zip",
            self.directory / ".pcl-repair" / "official.zip",
        )
        for output in paths:
            with self.subTest(output=output.name), patch.object(official, "_read") as read:
                with self.assertRaises(official.OfficialDownloadError):
                    official.download("2.99.0.0", output)
                read.assert_not_called()
                self.assertFalse(output.exists())

    def test_reparse_parent_refused_without_network(self):
        original_stat = Path.stat
        directory = self.directory

        def observed_stat(path, *, follow_symlinks=True):
            info = original_stat(path, follow_symlinks=follow_symlinks)
            if path == directory and not follow_symlinks:
                return SimpleNamespace(st_mode=info.st_mode, st_file_attributes=0x400)
            return info

        with patch.object(Path, "stat", observed_stat), patch.object(official, "_read") as read:
            with self.assertRaises(official.OfficialDownloadError):
                official.download("2.99.0.0", self.output)
            read.assert_not_called()
        self.assertFalse(self.output.exists())

    def test_redirect_is_refused_instead_of_followed(self):
        with self.assertRaises(official.OfficialDownloadError):
            official._NoRedirect().redirect_request(
                None, None, 302, "Found", {}, "https://example.com/other"
            )

    def test_oversized_http_header_rejected_before_body_read(self):
        response = SimpleNamespace(status=200, headers={"Content-Length": "129"})
        context = unittest.mock.MagicMock()
        context.__enter__.return_value = response
        opener = SimpleNamespace(open=lambda *_args, **_kwargs: context)
        with patch.object(official, "build_opener", return_value=opener):
            with self.assertRaises(official.OfficialDownloadError):
                official._read("https://api.github.com/public", 128)

    def test_stream_size_limit_without_content_length(self):
        content = io.BytesIO(b"x" * 129)
        response = SimpleNamespace(status=200, headers={}, read=content.read)
        context = unittest.mock.MagicMock()
        context.__enter__.return_value = response
        opener = SimpleNamespace(open=lambda *_args, **_kwargs: context)
        with patch.object(official, "build_opener", return_value=opener):
            with self.assertRaises(official.OfficialDownloadError):
                official._read("https://api.github.com/public", 128)


if __name__ == "__main__":
    unittest.main()
