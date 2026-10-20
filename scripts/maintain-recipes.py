"""One-shot maintainer pipeline. The chat heartbeat supplies scheduling/notifications.

No downloaded scripts are executed. All launcher bytes stay in fresh Work directories.
Signing is a final, explicit phase after authenticated baseline and compatibility tests.
"""
import argparse
import copy
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import urllib.parse
import urllib.request
import uuid
import zipfile
from contextlib import contextmanager

ROOT = Path(__file__).resolve().parent.parent
REPOSITORY = "AFT-Sakee01/MC-PCLfixer"
UPSTREAM = "Meloong-Git/PCL"
KEY_NAME = "PCL-RecipePublisher-AFTSakee01-MCPCLfixer"
MAX_ARCHIVE = 64 * 1024 * 1024
MAX_EXE = 64 * 1024 * 1024
PROTECTED_SHA = "6044f82cf37f9c8cee746c537bf17dd31699e11d435edb6dbd8fd54b81c8ee5b"
PUBLISH_PATHS = ("feed/stable.json", "feed/maintenance-state.json",
                 "Docs/Maintenance/CHANGELOG.jsonl")
REQUIRED_CHECKS = {
    "profile_schema_and_fixed_scope", "official_input_is_restricted",
    "transform_scope_resources_and_output", "idempotent_transform",
    "changed_dependency_refused", "apply_matches_validated_transform",
    "backup_matches_exact_input", "restore_matches_exact_input",
    "profile_serialization_roundtrip", "in_memory_input_unchanged",
}


class Refused(RuntimeError):
    pass


def command(args, cwd=None, timeout=180):
    result = subprocess.run([str(a) for a in args], cwd=cwd or ROOT,
                            capture_output=True, text=True, encoding="utf-8",
                            errors="replace", timeout=timeout)
    if result.returncode:
        # Raw subprocess errors can include credentials, paths or downloaded text.
        # Keep a fixed operation identifier and exit code; never echo raw argv.
        raise Refused(f"{Path(str(args[0])).name} exited {result.returncode}")
    return result.stdout.strip()


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def utc(value):
    if not isinstance(value, str) or not value.endswith("Z"):
        raise Refused("Timestamp must be UTC")
    return dt.datetime.fromisoformat(value[:-1] + "+00:00")


def stamp(now):
    return now.astimezone(dt.timezone.utc).isoformat().replace("+00:00", "Z")


def safe_work_path(path, root=ROOT):
    path = Path(os.path.abspath(path))
    work = Path(os.path.abspath(root)) / "Work"
    if not path.is_relative_to(work) or path == work:
        raise Refused("Output must be inside Work")
    for item in (path, *path.parents):
        if item.name.lower() in ("backups", ".pcl-repair", "pcl2dla.zip"):
            raise Refused("Protected path")
        if item.exists() or item.is_symlink():
            info = item.lstat()
            if item.is_symlink() or getattr(info, "st_file_attributes", 0) & 0x400:
                raise Refused("Reparse path")
    return path


def write_new(path, data):
    path = safe_work_path(path)
    with path.open("xb") as stream:
        stream.write(data)


def write_json(path, data):
    write_new(path, (json.dumps(data, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))


def protected_inputs():
    # Never execute or unpack these; the hashes only attest to preservation.
    for path in (ROOT / "PCL2DLA.zip",
                 ROOT / "Backups/original-20261020-145100/PCL2DLA.zip"):
        if path.exists() and digest(path) != PROTECTED_SHA:
            raise Refused("Protected reference hash changed")


def parse_release(data):
    tag = data.get("tag_name", "")
    if (not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", tag)
            or data.get("draft") is not False or data.get("prerelease") is not False
            or type(data.get("id")) is not int or data["id"] < 1):
        raise Refused("Unexpected official stable release")
    if data.get("html_url") != f"https://github.com/{UPSTREAM}/releases/tag/{tag}":
        raise Refused("Unexpected official release URL")
    utc(data["published_at"])
    utc(data.get("updated_at", data["published_at"]))
    # GitHub currently lists no binary assets. Do not mistake source archives for
    # the official launcher. The maintained official distribution locator is separate.
    return {"tag": tag, "release_id": data["id"], "published_at": data["published_at"],
            "updated_at": data.get("updated_at", data["published_at"])}


def needs_refresh(catalog, now):
    return utc(catalog["expires_utc"]) <= now + dt.timedelta(days=7)


def merge_profile(catalog, profile, now):
    result = copy.deepcopy(catalog)
    if profile not in result["profiles"]:
        result["profiles"].insert(0, copy.deepcopy(profile))
    if len(result["profiles"]) > 16:
        # Eviction would strand users of an older launcher. It is a policy change,
        # not an automatic consequence of a catalog size limit.
        raise Refused("Catalog full; supported profiles must not be silently removed")
    result["sequence"] += 1
    result["issued_utc"] = stamp(now)
    result["expires_utc"] = stamp(now + dt.timedelta(days=30))
    return result


def extract_launcher(archive, output):
    output = Path(output)
    with zipfile.ZipFile(archive) as source:
        candidates = [entry for entry in source.infolist()
                      if entry.filename.replace("\\", "/").rsplit("/", 1)[-1]
                      == "Plain Craft Launcher 2.exe"]
        if len(candidates) != 1:
            raise Refused("Official archive must contain exactly one launcher")
        entry = candidates[0]
        if (entry.file_size < 1024 or entry.file_size > MAX_EXE
                or entry.flag_bits & 1 or stat.S_ISLNK(entry.external_attr >> 16)):
            raise Refused("Invalid launcher archive entry")
        # Read only the executable. Ignore account/settings/log/script entries and
        # never interpret ZIP entry paths as destination paths.
        created = False
        try:
            with output.open("xb") as outgoing:
                created = True
                with source.open(entry) as incoming:
                    total = 0
                    while block := incoming.read(65536):
                        total += len(block)
                        if total > MAX_EXE:
                            raise Refused("Launcher exceeds size limit")
                        outgoing.write(block)
            if total != entry.file_size:
                raise Refused("Incomplete launcher")
        except Exception:
            if created:
                output.unlink()  # Only this invocation's exclusively created file.
            raise
    return output


def download_official(release, output):
    """Fetch only the fixed official locator. Changes to this policy need review."""
    # Import only the source-workspace module, never code in downloaded archives.
    sys.path.insert(0, str(Path(__file__).parent))
    from official_pcl import download
    return download(release["tag"], output)


def publish_checkout(checkout, expected_head, run=command):
    actual = run(["git", "ls-remote", "origin", "refs/heads/main"], cwd=checkout).split()
    if not actual or actual[0] != expected_head:
        raise Refused("Remote advanced during validation; retry from new signed baseline")
    # A fresh checkout, an explicit path list and a non-force push prevent mixing
    # user edits or overwriting a concurrent publisher's history.
    changed = set(run(["git", "diff", "--name-only"], cwd=checkout).splitlines())
    staged = set(run(["git", "diff", "--cached", "--name-only"], cwd=checkout).splitlines())
    untracked = set(run(["git", "ls-files", "--others", "--exclude-standard"], cwd=checkout).splitlines())
    if (changed | staged | untracked) - set(PUBLISH_PATHS):
        raise Refused("Unexpected publication file")
    run(["git", "add", "--", *PUBLISH_PATHS], cwd=checkout)
    run(["git", "-c", "user.name=PCL recipe maintenance",
         "-c", "user.email=246036730+AFT-Sakee01@users.noreply.github.com",
         "commit", "-m", "Publish tested recipe catalog"], cwd=checkout)
    run(["git", "push", "origin", "HEAD:refs/heads/main"], cwd=checkout, timeout=120)
    return run(["git", "rev-parse", "HEAD"], cwd=checkout)


def sync_publication_baseline(expected_head, run=command):
    if run(["git", "status", "--porcelain"]):
        raise Refused("Commit validated maintainer changes before publication")
    local = run(["git", "rev-parse", "HEAD"])
    if local == expected_head:
        return
    # The previous run published from a separate checkout. A later run may
    # fast-forward only those data/history paths; it must never silently start
    # executing newly downloaded maintenance code or discard a local commit.
    run(["git", "fetch", "--no-tags", f"https://github.com/{REPOSITORY}.git", "main"], timeout=120)
    if run(["git", "rev-parse", "FETCH_HEAD"]) != expected_head:
        raise Refused("Remote advanced during baseline synchronization")
    changed = set(run(["git", "diff", "--name-only", local, expected_head]).splitlines())
    if changed - set(PUBLISH_PATHS):
        raise Refused("Remote maintenance code changed; review and synchronize source first")
    run(["git", "merge", "--ff-only", expected_head])


@contextmanager
def maintenance_lock():
    import msvcrt
    work = ROOT / "Work"
    work.mkdir(exist_ok=True)
    path = safe_work_path(work / "maintenance.lock")
    with path.open("a+b") as stream:
        if stream.tell() == 0:
            stream.write(b"\0")
            stream.flush()
        stream.seek(0)
        try:
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError:
            raise Refused("Another maintenance run is active") from None
        try:
            yield
        finally:
            stream.seek(0)
            msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)


def deployment_record(version, now, release, state):
    return {
        "schema_version": 1,
        "id": f"deploy-{now.strftime('%Y%m%dT%H%M%SZ')}-{version.replace('.', '-')}-feed-{state['sequence']}",
        "timestamp_utc": stamp(now),
        "timestamp_local": now.astimezone(dt.timezone(dt.timedelta(hours=9))).isoformat(),
        "timezone": "Asia/Tokyo", "version": version,
        "scope": "自动维护已验证配方目录", "module": "RecipeMaintainer",
        "change_type": "deployment", "author_model": "Codex",
        "diagnosis": "官方新发行或配方进入到期前七日续签窗口。",
        "method": "独立副本等价适配、回归检查、CNG签名、远端基线检查和非强制推送。",
        "changed_locations": list(PUBLISH_PATHS),
        "target_paths": [f"https://github.com/{REPOSITORY}/blob/main/feed/stable.json"],
        "backup_paths": ["Git parent commit retains previous signed feed"],
        "verification_commands": ["RecipeMaintainer prepare", "scripts/verify.ps1",
                                  "tests/test_maintainer.py", "FeedPublisher verify"],
        "verification_evidence": f"PCL {release['tag']}; target SHA256 {state['target_sha256']}; "
                                 f"candidate {state['profile_sha256']}; checks {state['checks_passed']}; "
                                 f"signed sequence {state['sequence']}; all required gates passed.",
        "residual_risks": "结构等价检查不替代任意未来业务逻辑的功能验证；未知语义变化停止发布。",
        "token_usage": {"input": None, "output": None, "total": None, "source": "platform_unavailable"},
    }


def pipeline(args, run_dir):
    args.stage = "baseline"
    args.publication_attempted = False
    dotnet = ROOT / ".dotnet/dotnet.exe"
    if not dotnet.is_file():
        raise Refused("Local .NET SDK unavailable")
    maintainer = ROOT / "tools/recipe-maintainer/RecipeMaintainer.csproj"
    publisher = ROOT / "tools/feed-publisher/FeedPublisher.csproj"

    def tool(project, *parts):
        try:
            return command([dotnet, "run", "--project", project, "-c", "Release", "--", *parts],
                           timeout=300)
        except Refused:
            raise Refused(f"{project.stem}/{parts[0]} gate failed") from None

    # Pull a frozen remote commit; never reset/clean the user's checkout.
    checkout = run_dir / "publication"
    command(["git", "clone", "--depth", "1", "--branch", "main",
             f"https://github.com/{REPOSITORY}.git", checkout], timeout=120)
    head = command(["git", "rev-parse", "HEAD"], cwd=checkout)
    if args.publish:
        sync_publication_baseline(head)
    release = parse_release(json.loads(command(["gh", "api", f"repos/{UPSTREAM}/releases/latest"])))
    verified = run_dir / "verified-catalog.json"
    tool(maintainer, "catalog", checkout / "feed/stable.json", ROOT / "feed/publisher-public.pem", verified)
    catalog = json.loads(verified.read_text(encoding="utf-8-sig"))
    now = dt.datetime.now(dt.timezone.utc)
    state_path = checkout / "feed/maintenance-state.json"
    old_state = json.loads(state_path.read_text(encoding="utf-8")) if state_path.exists() else {}
    if args.command == "check":
        return {"status": "checked", "release": release, "sequence": catalog["sequence"],
                "renewal_due": needs_refresh(catalog, now),
                "last_validated_release": old_state.get("release")}

    archive = run_dir / "official.zip"
    args.stage = "official_download"
    provenance = download_official(release, archive)
    target = extract_launcher(archive, run_dir / "Plain Craft Launcher 2.exe")
    candidate = run_dir / "candidate"
    args.stage = "candidate_validation"
    tool(maintainer, "prepare", target, checkout / "feed/stable.json",
         ROOT / "feed/publisher-public.pem", candidate)
    evidence = json.loads((candidate / "evidence.json").read_text(encoding="utf-8-sig"))
    profile_path = candidate / "profile.json"
    profile = json.loads(profile_path.read_text(encoding="utf-8-sig"))
    if (evidence.get("schema_version") != 1 or
            evidence["launcher_version"] != release["tag"] or evidence["target_sha256"] != digest(target)
            or evidence["profile_sha256"] != digest(profile_path)
            or evidence["checks_passed"] != len(REQUIRED_CHECKS)
            or set(evidence.get("check_names", [])) != REQUIRED_CHECKS
            or len(evidence["check_names"]) != len(REQUIRED_CHECKS)
            or evidence.get("mode") not in ("reviewed_exact", "proven_nop_insertion")
            or evidence.get("patched_sha256") != digest(candidate / "patched-test.exe")):
        raise Refused("Candidate evidence does not match official release")
    known = (old_state.get("release") == release and
             old_state.get("target_sha256") == digest(target) and profile in catalog["profiles"])
    if known and not needs_refresh(catalog, now) and not args.force_refresh:
        return {"status": "unchanged", "release": release, "sequence": catalog["sequence"]}

    # Prepare's NOP-only equivalence proof is necessary, but does not replace the
    # patcher's independent integrity/restore/loader/signature regression suite.
    args.stage = "regression_tests"
    command(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
             ROOT / "scripts/verify.ps1"], timeout=900)
    next_catalog = merge_profile(catalog, profile, dt.datetime.now(dt.timezone.utc))
    catalog_path = run_dir / "catalog.json"
    write_json(catalog_path, next_catalog)
    state = {"schema_version": 1, "timestamp_utc": stamp(now), "release": release,
             "target_sha256": digest(target), "profile_sha256": digest(profile_path),
             "checks_passed": evidence["checks_passed"], "mode": evidence["mode"],
             "sequence": next_catalog["sequence"], "baseline_commit": head,
             "provenance": provenance}
    if not args.publish:
        return {"status": "validated", "release": release, "candidate": state, "published": False}

    # Ensure source/tests did not change during validation. The caller may prepare
    # a code adaptation first, but must commit/push it before invoking publication.
    if command(["git", "status", "--porcelain"]):
        raise Refused("Commit validated maintainer changes before publication")
    if command(["git", "rev-parse", "HEAD"]) != head:
        raise Refused("Maintainer checkout must match the validated remote baseline")
    protected_inputs()
    signed = run_dir / "stable-new.json"
    args.stage = "signing"
    tool(publisher, "sign", KEY_NAME, catalog_path, signed)
    tool(publisher, "verify", signed, ROOT / "feed/publisher-public.pem")
    (checkout / "feed/stable.json").write_bytes(signed.read_bytes())
    state["feed_sha256"] = digest(signed)
    state_path.write_text(json.dumps(state, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    version = re.search(r'const string Version="([\d.]+)"',
                        (ROOT / "src/Program.cs").read_text(encoding="utf-8-sig")).group(1)
    record = deployment_record(version, now, release, state)
    with (checkout / "Docs/Maintenance/CHANGELOG.jsonl").open("a", encoding="utf-8") as history:
        history.write(json.dumps(record, ensure_ascii=False) + "\n")
    command([sys.executable, "-X", "utf8", ROOT / "Docs/validate_docs.py",
             "--root", checkout, "--strict"], timeout=120)
    args.stage = "publication"
    args.publication_attempted = True
    commit = publish_checkout(checkout, head)
    # Verify the committed bytes from GitHub, avoiding raw CDN cache ambiguity.
    remote_file = json.loads(command(["gh", "api",
        f"repos/{REPOSITORY}/contents/feed/stable.json?ref={commit}"]))
    import base64
    live = base64.b64decode(remote_file["content"], validate=False)
    if hashlib.sha256(live).hexdigest() != state["feed_sha256"]:
        raise Refused("Published feed verification failed")
    return {"status": "published", "release": release, "sequence": next_catalog["sequence"],
            "commit": commit, "feed_sha256": state["feed_sha256"]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("check", "run"))
    parser.add_argument("--publish", action="store_true", help="Sign/push only after all gates pass")
    parser.add_argument("--force-refresh", action="store_true", help="Exercise renewal even before expiry")
    args = parser.parse_args()
    if args.command == "check" and (args.publish or args.force_refresh):
        parser.error("check never signs or publishes")
    run_dir = None
    try:
        if os.name != "nt":
            raise Refused("Maintenance/signing requires the Windows publisher account")
        with maintenance_lock():
            protected_inputs()
            run_dir = safe_work_path(ROOT / "Work" / ("maintenance-" + uuid.uuid4().hex))
            run_dir.mkdir()
            result = pipeline(args, run_dir)
            protected_inputs()
            result.update(schema_version=1, timestamp_utc=stamp(dt.datetime.now(dt.timezone.utc)))
            write_json(run_dir / "result.json", result)
            print(json.dumps(result, ensure_ascii=False))
        return 0
    except Exception as error:
        result = {"schema_version": 1, "timestamp_utc": stamp(dt.datetime.now(dt.timezone.utc)),
                  "status": "failed", "error_type": type(error).__name__,
                  "reason": str(error) if isinstance(error, Refused) else "Maintenance operation failed",
                  "stage": getattr(args, "stage", "initialization"),
                  "published": "unknown; inspect remote commit" if getattr(args, "publication_attempted", False) else False}
        if run_dir:
            try:
                write_json(run_dir / "result.json", result)
            except Exception:
                pass
        print(json.dumps(result, ensure_ascii=False))
        return 2


if __name__ == "__main__":
    sys.exit(main())
