"""Package published helpers and sources without user data or test downloads."""
import hashlib
import pathlib
import re
import tarfile
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
VERSION = re.search(r'const string Version="([\d.]+)"',
                    (ROOT / "src/Program.cs").read_text(encoding="utf-8-sig")).group(1)
out = ROOT / "Artifacts/Packages"
out.mkdir(parents=True, exist_ok=True)
licences = sorted((ROOT / "licenses").glob("*.txt"))
notice = """PCL (Local Download Repair) {version}
PCL 原作者：龙腾猫跃 https://github.com/Meloong-Git/PCL
赞助作者：https://meloong.com/afd/a/LTCat
本工具是第三方独立修改，不是官方发行。

Windows：把本工具放在 Plain Craft Launcher 2.exe 旁，关闭 PCL 后双击本工具。
其他文件名：PCL-Download-Repair.exe run "你的PCL.exe"
撤销补丁：PCL-Download-Repair.exe restore "你的PCL.exe"
首次运行辅助程序会启用自动加载；此后直接打开 PCL 或官方更新后重启，都会自动修复兼容版本。
内置配方失配时会从预设公开源获取签名新配方，无需手动绑定地址。
维护者尚未发布适配配方时会提示并停止；稍后直接打开 PCL 即可重试。
项目与源码：https://github.com/AFT-Sakee01/MC-PCLfixer
关闭自动加载：PCL-Download-Repair.exe disable-auto "你的PCL.exe"
未知版本拒绝修改。保留 helper、PCL.Repair.Bootstrap.dll、EXE旁的.config/.pcl-repair.json和.pcl-repair备份目录。
不要操作唯一的原始 ZIP。

Ubuntu：chmod +x PCL-Download-Repair
./PCL-Download-Repair apply "/path/to/Plain Craft Launcher 2.exe"
Linux 只能修复 Windows EXE；PCL 本体是 Windows WPF 应用。
完整说明与源代码见同批 Source 包的 README.md。
""".format(version=VERSION).encode("utf-8")

for rid in ("win-arm64", "win-x64", "linux-arm64", "linux-x64"):
    binary = ROOT / "Artifacts" / rid / ("PCL-Download-Repair.exe" if rid.startswith("win") else "PCL-Download-Repair")
    if not binary.is_file():
        raise FileNotFoundError(f"Publish {rid} first")
    stem = f"PCL-Local-Download-Repair-{VERSION}-{rid}"
    if rid.startswith("win"):
        with zipfile.ZipFile(out / (stem + ".zip"), "w", zipfile.ZIP_DEFLATED) as z:
            z.write(binary, binary.name)
            z.writestr("使用说明.txt", notice)
            z.write(ROOT / "feed/publisher-public.pem", "publisher-public.pem")
            for path in licences:
                z.write(path, "licenses/" + path.name)
    else:
        import io
        with tarfile.open(out / (stem + ".tar.gz"), "w:gz") as tar:
            info = tar.gettarinfo(str(binary), binary.name)
            info.mode = 0o755
            with binary.open("rb") as handle:
                tar.addfile(info, handle)
            info = tarfile.TarInfo("使用说明.txt")
            info.size, info.mode = len(notice), 0o644
            tar.addfile(info, io.BytesIO(notice))
            tar.add(ROOT / "feed/publisher-public.pem", arcname="publisher-public.pem")
            for path in licences:
                tar.add(path, arcname="licenses/" + path.name)
    print("Packaged", rid)

# Explicit allowlist: never include the old ZIP, backups, settings, account data,
# build caches, downloaded games, logs or native test fixtures in source packages.
with zipfile.ZipFile(out / f"PCL-Local-Download-Repair-{VERSION}-Source.zip",
                     "w", zipfile.ZIP_DEFLATED) as z:
    for name in ("README.md", "AGENTS.md", "CLAUDE.md", ".gitignore", ".gitattributes"):
        z.write(ROOT / name, name)
    for name in ("src", "bootstrap", "tests", "tools", "scripts", "recipes", "feed", "licenses", "Docs"):
        for path in sorted((ROOT / name).rglob("*")):
            rel = path.relative_to(ROOT)
            if path.is_file() and not set(rel.parts).intersection({"bin", "obj", "vendor", "__pycache__"}):
                z.write(path, rel.as_posix())
hashes = [hashlib.sha256(path.read_bytes()).hexdigest() + "  " + path.name
          for path in sorted(out.iterdir()) if path.suffix in (".zip", ".gz")]
(out / "SHA256SUMS.txt").write_text("\n".join(hashes) + "\n", encoding="utf-8")
print("Packaged sources and SHA256SUMS.txt")
