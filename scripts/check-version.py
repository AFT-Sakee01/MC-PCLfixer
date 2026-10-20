"""Validate the declared release version in source, build metadata and documents."""
import pathlib
import re
import xml.etree.ElementTree as ET

root = pathlib.Path(__file__).resolve().parent.parent
program = (root / "src/Program.cs").read_text(encoding="utf-8-sig")
version = re.search(r'const string Version="([\d.]+)"', program).group(1)
assert re.findall(r'Assembly(?:File)?Version\("([^"]+)"\)', program) == [version, version]
for filename in ("src/PclRepair.csproj", "bootstrap/PclRepair.Bootstrap.csproj", "tools/feed-publisher/FeedPublisher.csproj"):
    project = ET.parse(root / filename).getroot()
    for field in ("Version", "AssemblyVersion", "FileVersion"):
        assert project.find(f"PropertyGroup/{field}").text == version, (filename, field)
assert version in (root / "README.md").read_text(encoding="utf-8-sig")
print("Version consistent:", version)
