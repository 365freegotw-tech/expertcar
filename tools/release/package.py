#!/usr/bin/env python3
"""Package a clean win-x64 single-file publish and the rc2 demo archive."""
import hashlib
import re
import shutil
import sys
import tempfile
import zipfile
from pathlib import Path

repo = Path(__file__).resolve().parents[2]
publish, demo_zip, output = (Path(value).resolve() for value in sys.argv[1:4])
output.mkdir(parents=True, exist_ok=True)
assert demo_zip.is_file() and zipfile.is_zipfile(demo_zip), "Provide the rc1 synthetic-shop-demo.zip"
with tempfile.TemporaryDirectory(prefix="expertcar-rc2-") as temporary:
    app = Path(temporary) / "ExpertCar"
    shutil.copytree(publish, app)
    assert (app / "ExpertCar.App.exe").is_file(), "PublishSingleFile output is missing"
    (app / "ExpertCar.App.exe").rename(app / "ExpertCar.exe")
    assert not list(app.glob("*.dll")), "Single-file publish left top-level DLLs"
    (app / "Rules").rename(app / "tools/Rules")
    for name in ("mdb-export.exe", "mdb-schema.exe", "mdb-tables.exe"):
        shutil.copy2(repo / "tools/mdbtools/windows" / name, app / "tools/mdbtools" / name)
    for name in ("LICENSE", "THIRD-PARTY-NOTICES.md"):
        destination = app / "tools/legal" / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(repo / name, destination)
    shutil.copytree(repo / "licenses", app / "tools/legal/licenses")
    notice = (app / "tools/legal/THIRD-PARTY-NOTICES.md").read_text().replace("0.2.0-rc1", "0.2.0-rc2")
    notice = re.sub(r"(?<!/)licenses/", "tools/legal/licenses/", notice)
    notice = notice.replace("docs/MDBTOOLS-WINDOWS.md", "tools/mdbtools/PROVENANCE.json 與 tools/mdbtools/build/")
    (app / "tools/legal/THIRD-PARTY-NOTICES.md").write_text(notice)
    guide = (repo / "使用說明-請先點我.txt").read_bytes()
    assert guide.startswith(b"\xef\xbb\xbf") and guide.count(b"\n") == guide.count(b"\r\n"), "Guide must use UTF-8 BOM and CRLF"
    (app / "使用說明-請先點我.txt").write_bytes(guide)
    required = ["ExpertCar.exe", "使用說明-請先點我.txt", "tools/Rules/customer-vehicle-v1.json",
                "tools/mdbtools/mdb-export.exe", "tools/mdbtools/mdb-schema.exe", "tools/mdbtools/mdb-tables.exe",
                "tools/mdbtools/sources/mdbtools-1.0.1.tar.gz", "tools/mdbtools/sources/libiconv-1.18.tar.gz",
                "tools/legal/licenses/SQLitePCLRaw-LICENSE"]
    assert all((app / name).is_file() for name in required), "Missing required release content"
    for line in (app / "tools/mdbtools/SHA256SUMS").read_text().splitlines():
        digest, name = line.split()
        assert hashlib.sha256((app / "tools/mdbtools" / name).read_bytes()).hexdigest() == digest
    forbidden = [Path.home().as_posix().encode(), b"liuxspro/mdbtools-win-build-action"]
    manifest = []
    for path in sorted(app.rglob("*")):
        if not path.is_file():
            continue
        assert path.suffix.lower() not in {".db", ".sqlite", ".sqlite3", ".mdb", ".dbf", ".pdb"}, path
        assert path.name not in {"README.md", "WINDOWS-ACCEPTANCE.md", "ui-preferences-v02.json"}, path
        data = path.read_bytes()
        assert not any(value in data or value.decode().encode("utf-16-le") in data for value in forbidden), path
        manifest.append(hashlib.sha256(data).hexdigest() + "  " + path.relative_to(app).as_posix())
    (app / "tools/legal/ARTIFACT-MANIFEST.sha256").write_text("\n".join(manifest) + "\n")
    app_zip = output / "ExpertCar-0.2.0-rc2-win-x64.zip"
    with zipfile.ZipFile(app_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        archive.writestr("ExpertCar/", "")
        for path in sorted(app.rglob("*")):
            if path.is_file():
                archive.write(path, (Path("ExpertCar") / path.relative_to(app)).as_posix())

sample_copy = output / "synthetic-shop-demo.zip"
demo_readme = (repo / "tools/release/README-DEMO.txt").read_bytes()
assert demo_readme.startswith(b"\xef\xbb\xbf") and demo_readme.count(b"\n") == demo_readme.count(b"\r\n")
with zipfile.ZipFile(demo_zip) as source, zipfile.ZipFile(sample_copy, "w") as destination:
    for entry in source.infolist():
        destination.writestr(entry, demo_readme if entry.filename == "README-DEMO.txt" else source.read(entry.filename))
with zipfile.ZipFile(app_zip) as archive:
    assert archive.testzip() is None
with zipfile.ZipFile(sample_copy) as archive:
    assert archive.testzip() is None
sums = "".join(hashlib.sha256(path.read_bytes()).hexdigest() + "  " + path.name + "\n"
               for path in (app_zip, sample_copy))
(output / "SHA256SUMS").write_text(sums)
print(sums, end="")
print(f"guide_source=repository release_files={len(manifest)} app_bytes={app_zip.stat().st_size} demo_bytes={sample_copy.stat().st_size}")
print("PASS zip CRC / required content / native hashes / no shop database / no host paths")
