# Windows x64 MDBTools 打包方案

ExpertCar.Import 以獨立程序呼叫 `mdb-tables`、`mdb-schema`、`mdb-export`。工具目錄依序取建構參數 `toolsDirectory`、環境變數 `EXPERTCAR_MDBTOOLS_DIR`、Windows 程式目錄下的 `tools\mdbtools\`；目錄內缺少某命令時才退回 `PATH`。`mdb-export` 的標準輸出直接解析，不寫 CSV；Jet 3 輸入明確設 `MDB_JET3_CHARSET=CP950`，輸出設 `MDBICONV=UTF-8`。Linux 可用發行版的 `mdbtools` 套件，執行檔在 `PATH` 時不必指定目錄。完整本機測試方式見 [README](../README.md#建置及測試)。

## 建置 Windows x64 工具

[MDBTools 官方 README](https://github.com/mdbtools/mdbtools/blob/dev/README.md)列出 MinGW 交叉編譯 `--host=x86_64-w64-mingw32 --disable-shared --enable-static`；[官方 1.0.1 發行頁](https://github.com/mdbtools/mdbtools/releases/tag/v1.0.1)目前只列原始碼 tar/zip，沒有官方 Windows 執行檔。建議在 MSYS2 **MINGW64** shell 用 1.0.1 原始碼建置；固定下載檔與 SHA256 後再納入發行流水線：

```sh
pacman -S --needed base-devel mingw-w64-x86_64-toolchain mingw-w64-x86_64-pkgconf \
  mingw-w64-x86_64-glib2 mingw-w64-x86_64-libiconv bison flex
tar -xf mdbtools-1.0.1.tar.gz
cd mdbtools-1.0.1
./configure --host=x86_64-w64-mingw32 --disable-shared --enable-static
make -j2
```

正式安裝目錄只放需要的三個 `.exe` 及其 `ldd` 顯示的非系統 DLL，從**同一建置**取得；不用來源不明的單一 `mdb-export.exe` 取代整套。`mdb-tables -1`、`mdb-schema -T CUSTOMER ... mysql`、`mdb-export -H ... CUSTOMER` 都要在 Windows x64 實機跑合成檔與已授權樣本。若選現成社群釋出版，也要先固定發行者、版本、SHA256、相符完整原始碼與 DLL 來源，再實測三個命令；目前不將社群檔當作已驗證供應鏈。

## 發行必備

MDBTools 的命令列工具為 **GPL**，`libmdb`、`libmdbsql`、`libmdbodbc` 為 **LGPL**；依實際打包版本攜帶 `COPYING`、`COPYING.LIB`、完整相符來源、修改補丁、建置腳本與依賴授權，並提供使用者取得來源的方式。只給上游連結不足以代替相符來源。獨立程序邊界不自動決定整體授權相容性，發行前要核對最終組合與條款；主程式的 MIT 聲明不能覆蓋附帶工具。

Windows 實機待驗：MSYS2 建置成功及所有 DLL 齊備、Jet 3 cp950／Jet 4 中文輸出、CSV 換行與引號、逾時終止子程序、檔名含空白、離線安裝後執行、GPL／LGPL 發行包完整性。
