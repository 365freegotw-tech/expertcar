# Windows x64 MDBTools 來源與重建

0.2.0-rc2 使用官方 MDBTools 1.0.1 原始碼交叉編譯；未採用第三方 GitHub Action 成品。

- MDBTools 官方發行：https://github.com/mdbtools/mdbtools/releases/tag/v1.0.1
- 原始碼：https://github.com/mdbtools/mdbtools/releases/download/v1.0.1/mdbtools-1.0.1.tar.gz
- SHA256：`ff9c425a88bc20bf9318a332eec50b17e77896eef65a0e69415ccb4e396d1812`
- GNU libiconv 1.18：https://ftp.gnu.org/pub/gnu/libiconv/libiconv-1.18.tar.gz
- SHA256：`3b08f5f4f9b4eb82f151a7040bfd6fe6c6fb922efe4b1659c66ea933276965e8`
- libiconv `.sig` 以 GNU 官方 `https://ftp.gnu.org/gnu/gnu-keyring.gpg` 驗證成功，簽署指紋 `9001B85AF9E1B83DF1BDA942F5BE8B267C6A406D`（Bruno Haible）。MDBTools 發行頁未提供 detached signature；固定官方 HTTPS 來源與上述 SHA256，未聲稱驗過其上游簽章。

工具鏈為 Ubuntu 24.04 官方 archive 的 MinGW-w64 GCC 13.2.0 POSIX、binutils 2.41.90、MinGW headers 11.0.1；所有確切 deb 版本及 SHA256 記於 `tools/mdbtools/build/bootstrap-toolchain.sh` 與 `toolchain-SHA256SUMS`。僅下載並解壓，不需要系統安裝或 sudo。

```bash
mkdir toolchain-work
cd toolchain-work
bash ../expertcar/tools/mdbtools/build/bootstrap-toolchain.sh
export PATH="$PWD/root/usr/bin:$PATH"
cd ../expertcar
bash tools/mdbtools/build/build-windows.sh ../native-build ../native-output
```

完整 configure／make／strip 命令在腳本內。`mingw-compat.patch` 同時修正 configure.ac 與發行包 configure：移除 `_spawnv=spawnv` 巨集與錯誤放進全域 `LDFLAGS` 的 libtool `-no-undefined`，locale 型別採 Windows `_locale_t`。舊版因 GCC 不接受 `-no-undefined`，iconv 探測失敗（`HAVE_ICONV_H=0`），即使 configure 摘要顯示 enabled，EXE 仍輸出 Big5 與 ISO-8859-1 檔頭。新版顯式指定靜態 libiconv 的標頭、函式庫，並要求 `HAVE_ICONV=1`、`HAVE_ICONV_H=1`、`LIBS` 含 `-liconv`，否則建置中止。停用 SQL、ODBC、GLib、手冊，只建匯入需要的三個工具。libiconv、libcharset、compiler runtime 均靜態連結；`objdump -p` 檢查三個 EXE 只依賴 Windows 的 KERNEL32.dll、msvcrt.dll。PE timestamp 固定，移除除錯資訊並使用 prefix map。

發行包的 `tools/mdbtools/sources` 包含完整相符原始碼 tarball 與 libiconv 簽章；`build` 包含修補、重建腳本及工具鏈雜湊；`COPYING`、`COPYING.LIB` 與 `licenses` 提供 GPL／LGPL／MinGW／GCC 條款。ExpertCar 以子程序呼叫工具。兩個不同目錄的乾淨建置產生相同的三個 SHA256。未 strip 的 EXE 由 `nm` 查到 `libiconv_open`、`mdb_iconv_init`，strip 後仍含 `MDBICONV`、`MDB_JET3_CHARSET` 字串。詳細來源、雜湊及相依記錄見 PROVENANCE.json。

執行時路徑：建構參數 `toolsDirectory` → `EXPERTCAR_MDBTOOLS_DIR` → Windows 程式旁 `tools/mdbtools`；指定位置缺檔才退回 PATH。設定 `MDB_JET3_CHARSET=CP950`、`MDBICONV=UTF-8`。同一 1.0.1 原始碼及相同功能 configure 旗標在 Linux 原生建置後，`mdb-schema -T PreRCarFix` 實測顯示 `encoding UTF-8`、`估價單號`、`車牌號碼`。此機無 Wine；Windows EXE 的實際轉碼結果仍須站長實機驗收。程式另以原始位元組檢查 UTF-8，必要時以 CP950 解碼。
