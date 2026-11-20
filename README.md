# 汽修賺錢專家（ExpertCar）

[汽修賺錢專家官方網站](https://keelungit.com/expertcar/)提供產品說明與 Windows 下載。ExpertCar 是汽修廠單機使用的免費開源管理軟體，目前版本為 0.1.0。

## 目前功能

- Windows 10／11 x64 的 WPF 畫面：客戶與車輛、保養與驗車提醒、常用項目與零件目錄、店家設定。
- SQLite 本機資料庫；工單與金額規則已建立，完整工單操作及列印仍在開發中。
- 舊資料匯入精靈：先掃描、預覽與核對，再備份並以交易方式匯入支援的資料。

## Windows 下載與執行

從[官方下載頁](https://keelungit.com/expertcar/#download)下載 `ExpertCar-0.1-win-x64.zip`，解壓縮整個資料夾後執行 `ExpertCar.App.exe`。ZIP 已包含 .NET 執行環境，不需另行安裝。這個版本尚未數位簽章；若 Windows SmartScreen 顯示不明發行者，先核對下載來源和官方頁列出的 SHA256，確認後選「其他資訊」→「仍要執行」。Windows 實機相容性尚未全面驗證，重要資料請先備份。

資料庫位於 `%LOCALAPPDATA%\ExpertCar\expertcar.db`，與解壓縮的程式資料夾分開。資料庫升級前會在同一使用者資料目錄的 `backups` 子目錄建立備份；仍請自行保留重要資料的額外備份。

## 舊資料匯入

可匯入部分 DBF／MDB 舊資料結構，包含部分 SuperCar 資料。實際支援欄位取決於來源結構與預覽結果；請先複製舊資料，用複本掃描、核對待處理項目，再確認匯入。MDB 匯入可能需要自行安裝 MDBTools；本版 ZIP **不附** MDBTools 執行檔或函式庫。SuperCar 為其權利人之商標，本軟體與 SuperCar 及其權利人沒有關聯、合作或背書關係。

## 建置與測試

需要 .NET 10 SDK。Windows 可直接建置與執行；Linux 可交叉編譯 WPF，但不能在 Linux 執行 Windows 畫面。

```sh
dotnet build ExpertCar.slnx
dotnet test ExpertCar.Tests/ExpertCar.Tests.csproj
```

MDB 整合測試需要 `mdb-tables`、`mdb-schema`、`mdb-export` 在 `PATH`，或設定 `EXPERTCAR_MDBTOOLS_DIR` 指向這三個命令所在目錄；未提供時，相關測試會跳過。Windows MDBTools 建置與散布說明見 [文件](docs/MDBTOOLS-WINDOWS.md)。`ExpertCar.Tests/Fixtures/` 的 MDB／DBF 均由合成資料產生，不含真實客戶資料。

## 專案結構

- `ExpertCar.App`：WPF 畫面。
- `ExpertCar.Core`：資料模型、工單狀態與計價規則。
- `ExpertCar.Data`：SQLite 儲存與資料庫升級。
- `ExpertCar.Import`：唯讀舊檔掃描、預覽及匯入。
- `ExpertCar.Tests`：核心、資料與匯入測試。

## 授權與支援

程式以 [MIT 授權](LICENSE)公開，著作權人為 keelungIT。第三方元件與 MDBTools 散布條件見 [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES.md)。使用說明與後續版本請見[keelungIT 支援頁](https://keelungit.com/expertcar/)。
