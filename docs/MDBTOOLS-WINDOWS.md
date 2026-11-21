# Windows x64 MDBTools 來源與重建

0.2.0-rc1 不採用第三方 GitHub Action 成品，也不保留 250d WIP 來源不明的 EXE／DLL。本輪查詢 repo.msys2.org/mingw/mingw64 索引未找到 mdbtools，因此選用工單允許的官方 1.0.1 原始碼交叉編譯方案。

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

完整 configure／make／strip 命令在腳本內。`mingw-compat.patch` 同時修正 configure.ac 與發行包 configure：移除與 MinGW 11 標頭衝突的 `_spawnv=spawnv` 巨集，locale 型別採 Windows `_locale_t`。停用 SQL、ODBC、GLib、手冊，只建匯入需要的三個工具，保留 iconv CP950／UTF-8 轉換。libiconv、libcharset、compiler runtime 均靜態連結；`objdump -p` 檢查三個 EXE 只依賴 Windows 的 KERNEL32.dll、msvcrt.dll，無其他 DLL。PE timestamp 固定為 SOURCE_DATE_EPOCH=1734307200，移除除錯資訊並使用 prefix map。

發行包的 `tools/mdbtools/sources` 包含完整相符原始碼 tarball 與 libiconv 簽章；`build` 包含修補、重建腳本及工具鏈雜湊；`COPYING`、`COPYING.LIB` 與 `licenses` 提供 GPL／LGPL／MinGW／GCC 條款。可自行修改並按其授權重建與替換三個獨立命令；ExpertCar 以子程序呼叫，不載入其函式庫。兩個不同目錄的乾淨建置產生相同的三個 SHA256；詳細來源與相依記錄見 PROVENANCE.json。最終二進位雜湊見 `tools/mdbtools/SHA256SUMS`（repo 內位於 windows 子目錄）。

執行時路徑：建構參數 `toolsDirectory` → `EXPERTCAR_MDBTOOLS_DIR` → Windows 程式旁 `tools/mdbtools`；指定位置缺檔才退回 PATH。`MDB_JET3_CHARSET=CP950`、`MDBICONV=UTF-8`。Linux 驗證使用 Ubuntu 原生 MDBTools；未執行 Windows EXE，離線執行、中文與 DLL 載入須依 Windows 實機清單驗收。
