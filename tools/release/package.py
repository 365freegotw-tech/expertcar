#!/usr/bin/env python3
"""Package an existing clean win-x64 publish directory and an authorized synthetic shop."""
import hashlib
import json
import shutil
import sys
import zipfile
from pathlib import Path

repo = Path(__file__).resolve().parents[2]
publish, synthetic, output = map(lambda value: Path(value).resolve(), sys.argv[1:4])
output.mkdir(parents=True, exist_ok=True)
for name in ("LICENSE", "README.md", "THIRD-PARTY-NOTICES.md"):
    shutil.copy2(repo / name, publish / name)
shutil.copytree(repo / "licenses", publish / "licenses", dirs_exist_ok=True)
(publish / "docs").mkdir(exist_ok=True)
for name in ("MDBTOOLS-WINDOWS.md", "WINDOWS-ACCEPTANCE.md"):
    shutil.copy2(repo / "docs" / name, publish / "docs" / name)
shutil.copy2(repo / "tools/mdbtools/windows/PROVENANCE.json", publish / "tools/mdbtools/PROVENANCE.json")
required = ["ExpertCar.App.exe", "ExpertCar.App.dll", "coreclr.dll", "PresentationFramework.dll",
            "tools/mdbtools/mdb-export.exe", "tools/mdbtools/mdb-schema.exe", "tools/mdbtools/mdb-tables.exe",
            "tools/mdbtools/sources/mdbtools-1.0.1.tar.gz", "tools/mdbtools/sources/libiconv-1.18.tar.gz",
            "licenses/SQLitePCLRaw-LICENSE", "docs/WINDOWS-ACCEPTANCE.md"]
assert all((publish / name).is_file() for name in required), "Missing required release content"
for line in (publish / "tools/mdbtools/SHA256SUMS").read_text().splitlines():
    digest, name = line.split()
    assert hashlib.sha256((publish / "tools/mdbtools" / name).read_bytes()).hexdigest() == digest
assert not list((publish / "tools/mdbtools").glob("*.dll")), "Unexpected MDBTools DLL"

forbidden = [Path.home().parent.as_posix().encode() + b"/", b"liuxspro/mdbtools-win-build-action"]
upstream_dbf_sha256 = "d1e7b8c39518cd17ed000d63a0b568901dc5aa65b90f7871e6a5e7d62587b7fe"
def check(name, data):
    if name == "DbfDataReader.dll" and hashlib.sha256(data).hexdigest() == upstream_dbf_sha256:
        return  # Official NuGet binary contains its upstream CI build path.
    assert not any(value in data or value.decode().encode("utf-16-le") in data for value in forbidden), name

manifest = []
for path in sorted(publish.rglob("*")):
    if not path.is_file():
        continue
    assert path.suffix.lower() not in {".db", ".sqlite", ".sqlite3", ".mdb", ".dbf", ".pdb"}, path
    assert path.name not in {"ui-preferences-v02.json", "ARTIFACT-MANIFEST.sha256"}, path
    data = path.read_bytes()
    check(str(path.relative_to(publish)), data)
    manifest.append(hashlib.sha256(data).hexdigest() + "  " + path.relative_to(publish).as_posix())
(publish / "ARTIFACT-MANIFEST.sha256").write_text("\n".join(manifest) + "\n")
app_zip = output / "ExpertCar-0.2.0-rc1-win-x64.zip"
with zipfile.ZipFile(app_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
    for path in sorted(publish.rglob("*")):
        if path.is_file():
            archive.write(path, path.relative_to(publish).as_posix())

names = {"Base.mdb", "Item.MDB", "FixItem.MDB", "RCar089.MDB", "SCar089.MDB", "Fixrpt.mdb",
         "PreRCar089.MDB", "PreSCar089.MDB", "Cust_Pay.MDB", "WORK.MDB", "CUSTNAME.DBF"}
assert {path.name for path in synthetic.iterdir() if path.is_file()} == names
sample_zip = output / "synthetic-shop-demo.zip"
with zipfile.ZipFile(sample_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
    archive.writestr("README-DEMO.txt", """ExpertCar 0.2.0-rc1 合成店家示範資料（全部是假資料）
解壓後，在主程式的「舊資料匯入」選擇 synthetic 資料夾。
先演練、核對待確認清單、勾選確認，再備份提交。
空白資料庫首次預期：成功 117323、跳過 0、待確認 9、失敗 0。
再次演練：成功 0、跳過 117323、待確認 9、失敗 0。
測試車牌 T00-0001 有 10 張歷史工單。請使用獨立 Windows 測試帳號，避免混入店家正式資料。
此資料由專案合成產生器建立，不含原軟體程式碼或真實客人資料。
""")
    for name in sorted(names):
        data = (synthetic / name).read_bytes()
        check(name, data)
        archive.writestr("synthetic/" + name, data)
for archive_path in (app_zip, sample_zip):
    with zipfile.ZipFile(archive_path) as archive:
        assert archive.testzip() is None, archive_path
sums = "".join(hashlib.sha256(path.read_bytes()).hexdigest() + "  " + path.name + "\n" for path in (app_zip, sample_zip))
(output / "SHA256SUMS").write_text(sums)
print(sums, end="")
print(f"release_files={len(manifest)} app_bytes={app_zip.stat().st_size} demo_bytes={sample_zip.stat().st_size}")
print("PASS zip CRC / required content / native hashes / no shop database / no host paths / no unknown MDBTools DLL")
