"""Fetch the official release ZIP from its immutable upstream Git tree.

The upstream release currently has no binary assets. Its source tree contains
最新正式版.zip, so resolve the release tag to a commit before obtaining the file
metadata and content. The Git blob digest binds downloaded bytes to that tree;
it is not an independently signed publisher SHA-256.
"""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import stat
import time
from urllib.parse import quote
from urllib.request import HTTPRedirectHandler, Request, build_opener


REPOSITORY = "Meloong-Git/PCL"
ARCHIVE_NAME = "最新正式版.zip"
MAX_ARCHIVE_BYTES = 32 * 1024 * 1024
MAX_METADATA_BYTES = 8 * 1024 * 1024


class OfficialDownloadError(RuntimeError):
    """A public upstream download or its expected format failed validation."""


class _NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        # Both metadata and immutable raw URLs are constructed here. A changed
        # hosting scheme must be reviewed instead of expanding trust implicitly.
        raise OfficialDownloadError("Official source unexpectedly redirected.")


def _read(url: str, limit: int) -> bytes:
    request = Request(
        url,
        headers={
            "Accept": "application/vnd.github+json" if url.startswith("https://api.github.com/") else "application/octet-stream",
            "User-Agent": "PCL-Local-Download-Repair-Maintainer",
            "X-GitHub-Api-Version": "2022-11-28",
        },
    )
    deadline = time.monotonic() + 90
    with build_opener(_NoRedirect()).open(request, timeout=30) as response:
        if response.status != 200:
            raise OfficialDownloadError("Official source did not return HTTP 200.")
        length = response.headers.get("Content-Length")
        if length is not None and (not length.isdecimal() or int(length) > limit):
            raise OfficialDownloadError("Official source exceeds its size limit.")
        chunks: list[bytes] = []
        total = 0
        while True:
            if time.monotonic() > deadline:
                raise OfficialDownloadError("Official source read timed out.")
            chunk = response.read(min(65536, limit + 1 - total))
            if not chunk:
                break
            total += len(chunk)
            if total > limit:
                raise OfficialDownloadError("Official source exceeds its size limit.")
            chunks.append(chunk)
        if not total or (length is not None and total != int(length)):
            raise OfficialDownloadError("Official source response was incomplete.")
        return b"".join(chunks)


def _metadata(url: str) -> dict:
    value = json.loads(_read(url, MAX_METADATA_BYTES).decode("utf-8"))
    if not isinstance(value, dict):
        raise OfficialDownloadError("Official metadata format changed.")
    return value


def _output_path(output_path: str | os.PathLike[str]) -> Path:
    output = Path(os.path.abspath(output_path))
    if output.suffix.lower() != ".zip" or output.name.lower() == "pcl2dla.zip":
        raise OfficialDownloadError("Choose a new temporary ZIP output.")
    if not output.parent.is_dir() or os.path.lexists(output):
        raise OfficialDownloadError("Output must be new and its parent must exist.")
    # Check lexical ancestors before opening: resolving a junction first would
    # hide the original protected path. This helper never extracts the archive.
    for directory in output.parents:
        if directory.name.casefold() in {"backups", ".pcl-repair"}:
            raise OfficialDownloadError("Protected output directory.")
        info = directory.stat(follow_symlinks=False)
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise OfficialDownloadError("Reparse output directory is unsupported.")
    return output


def download(release_tag: str, output_path: str | os.PathLike[str]) -> dict:
    """Create a verified official ZIP; never overwrite, execute or extract it.

    Callers must still check the contained launcher's assembly version against
    release_tag and run the repair validation before considering publication.
    """
    if not isinstance(release_tag, str) or not re.fullmatch(r"\d{1,5}(?:\.\d{1,5}){3}", release_tag):
        raise OfficialDownloadError("Unsupported official release tag.")
    output = _output_path(output_path)
    commit = _metadata(f"https://api.github.com/repos/{REPOSITORY}/commits/{quote(release_tag, safe='')}")
    source_commit = commit.get("sha")
    if not isinstance(source_commit, str) or not re.fullmatch(r"[0-9a-f]{40}", source_commit):
        raise OfficialDownloadError("Official commit identity is invalid.")
    encoded_name = quote(ARCHIVE_NAME, safe="")
    source_url = f"https://raw.githubusercontent.com/{REPOSITORY}/{source_commit}/{encoded_name}"
    entry = _metadata(
        f"https://api.github.com/repos/{REPOSITORY}/contents/{encoded_name}?ref={source_commit}"
    )
    blob_sha, size = entry.get("sha"), entry.get("size")
    if (
        entry.get("type") != "file"
        or entry.get("name") != ARCHIVE_NAME
        or entry.get("path") != ARCHIVE_NAME
        or entry.get("download_url") not in (
            source_url,
            f"https://raw.githubusercontent.com/{REPOSITORY}/{source_commit}/{ARCHIVE_NAME}",
        )
        or not isinstance(blob_sha, str)
        or not re.fullmatch(r"[0-9a-f]{40}", blob_sha)
        or type(size) is not int
        or not 0 < size <= MAX_ARCHIVE_BYTES
    ):
        raise OfficialDownloadError("Official ZIP tree entry changed.")
    archive = _read(source_url, MAX_ARCHIVE_BYTES)
    # Git SHA-1 includes the object type/size header. Raw file SHA-1 would not
    # verify the tree entry. SHA-256 is recorded separately as local evidence.
    actual_blob = hashlib.sha1(b"blob " + str(len(archive)).encode("ascii") + b"\0" + archive).hexdigest()
    if len(archive) != size or actual_blob != blob_sha or not archive.startswith(b"PK\x03\x04"):
        raise OfficialDownloadError("Official ZIP did not match its pinned Git object.")
    zip_sha256 = hashlib.sha256(archive).hexdigest()
    # The existence check above is advisory; exclusive creation closes the race
    # with another maintainer. Only bytes already checked in memory reach disk.
    with output.open("xb") as destination:
        destination.write(archive)
        destination.flush()
        os.fsync(destination.fileno())
    return {
        "release_tag": release_tag,
        "source_repository": REPOSITORY,
        "source_commit": source_commit,
        "source_path": ARCHIVE_NAME,
        "source_url": source_url,
        "blob_sha": blob_sha,
        "zip_sha256": zip_sha256,
        "size": size,
    }
