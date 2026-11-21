# 汽修賺錢專家 ExpertCar 0.2.0-rc1

提供汽修店單機使用的客戶、車輛履歷、估價、工單、加修授權、收款與 A4 單據。資料存於本機 SQLite，關閉程式再開仍會保留。此版為試用候選版，請先用合成資料驗收。

## 安裝與開始使用

Windows x64：將 `ExpertCar-0.2.0-rc1-win-x64.zip` 完整解壓到可寫入的資料夾，雙擊 `ExpertCar.App.exe`，不必另外安裝 .NET。請保留整個 `tools` 資料夾；匯入 MDB 時會呼叫內附工具。程式不要求系統管理員權限。

資料庫位於 `%LOCALAPPDATA%\ExpertCar\expertcar.db`，備份位於同層 `backups`。更新程式前請關閉程式，另外複製整個 `%LOCALAPPDATA%\ExpertCar` 資料夾到安全位置。程式會在資料庫結構升級與匯入前建立備份；請自行安排額外異地備份。

先到「店家設定」填店名、電話、單據抬頭、地址、統編與工時費率。刷卡加成預設關閉；若店家決定啟用，須明確勾選並填費率。舊版 UI JSON 的工時費率、抬頭、地址、統編與建議零件會自動搬入資料庫，列印方向與導覽偏好仍留在 JSON。

## 如何匯入 SuperCar（賺錢高手）資料

SuperCar、賺錢高手為其權利人之商標，本軟體與其無任何關係。

1. 關閉原軟體，先完整備份原始資料。只使用店家有權處理的資料；在複製出的資料夾操作，保留原檔。
2. 初次試用請解壓另外提供的 `synthetic-shop-demo.zip`，選其中的 `synthetic` 資料夾。這些全是假資料。建議用獨立 Windows 測試帳號，避免混入既有店家資料。
3. 開啟「舊資料匯入」→「選擇來源資料夾」→「掃描並演練」。完整店家來源可包含 `Base.mdb`、`Item.MDB`、`FixItem.MDB`、`RCar089.MDB`、`SCar089.MDB`、`Fixrpt.mdb`、`PreRCar089.MDB`、`PreSCar089.MDB`、`Cust_Pay.MDB`、`WORK.MDB`、`CUSTNAME.DBF`；放在同一層。來源結構不符合時會拒絕或列出待確認，不會自動猜欄位。
4. 查看各檔案／資料表的掃描、可新增、跳過、待確認與失敗計數，並核對下方待確認清單。演練不提交資料；匯入可能需要幾分鐘，請等畫面完成。
5. 勾選「我已核對演練與待確認清單」後按「備份並提交匯入」。程式先備份目前資料庫，再用交易提交；畫面會顯示實際計數與備份位置。
6. 回首頁輸入車牌，開車輛履歷，抽查舊工單明細、金額與付款。保險與驗車欄位需人工確認，未知語意不自動啟用提醒。
7. 再按一次「掃描並演練」，相同來源應顯示成功 0，已匯入的資料列跳過，不會重複新增。待確認列仍需人工處理，重跑不等於問題已解決。修改過的來源請重新演練。

程式只讀取 MDB／DBF，不會修改原始檔；請勿直接覆蓋資料庫來合併兩家店。正式搬遷前應先抽查、試開單並保留原系統與備份。

## 開單、收款與列印

F2 搜尋車牌 → 查看履歷 → 用這台車開工單 → 加入工資、零件／料號、耗材、環保費、外包（另填廠商）→ 儲存估價。工資可用固定金額或工時分鐘 × 每小時費率。加修須記錄同意人、方式與時間；未同意項目不列入結帳。

按「客人同意，轉工單」→「開始施工」→「完工，待結帳」。約定交車日期為今天且已待結帳的單據會出現在首頁；「全站工單」可翻頁查看全部單據。結帳選開發票及付款方式，更新報價後可分多筆收款，應收歸零後才可完成交車。

估價單、維修單與收據預設 A4 直式，可在設定改橫式。預覽後按列印；輸出 PDF 選 Microsoft Print to PDF。發票須由店家另在合法發票系統開立，本程式單據不是統一發票。車輛過戶在履歷頁搜尋並選取現有新車主、填生效日，再確認；舊工單保留當時客戶。

## 免責與商標

SuperCar、賺錢高手為其權利人之商標，本軟體與其無任何關係。本專案為原創重寫，不包含原軟體程式碼或客人資料。匯入結果、來源欄位意義、稅務與收費政策須由店家核對。本軟體依 MIT 授權按現狀提供，無任何明示或默示擔保；請參閱 LICENSE 的完整條款。候選版請先於獨立帳號使用合成資料驗收。

MDBTools 命令列工具及其靜態依賴另受 GPL／LGPL 等條款規範，主程式 MIT 不取代它們的授權。相符原始碼、修補與重建方式隨包提供，見 THIRD-PARTY-NOTICES.md 與 tools/mdbtools。

## 開發與驗證

.NET 10 SDK；Linux 的 MDB 測試另需發行版 MDBTools（`mdb-tables`、`mdb-schema`、`mdb-export`）在 PATH，或用 `EXPERTCAR_MDBTOOLS_DIR` 指定目錄。使用本機解壓的發行版套件時也需設定其 `LD_LIBRARY_PATH`。

```bash
dotnet build ExpertCar.slnx -p:EnableWindowsTargeting=true
dotnet test ExpertCar.slnx -p:EnableWindowsTargeting=true
dotnet run --project tools/e2e-v02/E2E.csproj -- ./synthetic ./new-verification-output
dotnet publish ExpertCar.App/ExpertCar.App.csproj -c Release -r win-x64 --self-contained -p:EnableWindowsTargeting=true -p:DebugType=None -p:DebugSymbols=false
```

App 與無 WPF 驗證共用 `ExpertCarServices.OpenAsync`，使用 `ImportV02Service : SqliteV02Service`，全套 ViewModel 不依賴 WPF；Memory 只留測試專案。Windows 工具不由 Linux 測試代替實機驗收，請照 docs/WINDOWS-ACCEPTANCE.md 操作。
