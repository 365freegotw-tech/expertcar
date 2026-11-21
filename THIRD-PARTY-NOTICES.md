# Third-party notices — ExpertCar 0.2.0-rc1

主程式採 MIT（LICENSE）。附帶元件保有各自授權，本文件不取代完整條款。沒有採用第三方 GitHub Action 的 MDBTools 成品。

| 元件 | 版本／授權 | 來源及完整條款位置 |
| --- | --- | --- |
| MDBTools 三個命令列工具／libmdb | 1.0.1；工具 GPL-2.0-or-later、函式庫 LGPL-2.0-or-later | 官方 https://github.com/mdbtools/mdbtools/releases/tag/v1.0.1；包內 tools/mdbtools/COPYING、COPYING.LIB |
| GNU libiconv／libcharset（靜態連結） | 1.18；LGPL-2.1-or-later（未附 iconv 命令列程式） | https://www.gnu.org/software/libiconv/；包內 tools/mdbtools/licenses/libiconv-COPYING.LIB、libcharset-COPYING.LIB |
| MinGW-w64 runtime／GCC runtime | MinGW 11.0.1／GCC 13.2.0；各檔授權及 GCC Runtime Library Exception | https://www.mingw-w64.org/、https://gcc.gnu.org/；包內 tools/mdbtools/licenses/mingw-w64-copyright.txt、gcc-copyright.txt |
| DbfDataReader | NuGet 2.2.0；MIT；Copyright (c) 2016 Yellow Feather Ltd | https://github.com/yellowfeather/DbfDataReader；licenses/DbfDataReader-LICENSE |
| Microsoft.Data.Sqlite / Core | NuGet 10.0.15；MIT；.NET Foundation and Contributors | https://github.com/dotnet/efcore；licenses/EFCore-LICENSE.txt |
| SQLitePCLRaw | NuGet 2.1.13；Apache-2.0；SourceGear, LLC | https://github.com/ericsink/SQLitePCL.raw；licenses/SQLitePCLRaw-LICENSE |
| SQLite | public domain | https://sqlite.org/copyright.html；licenses/SQLite-copyright.txt |
| .NET runtime／Windows Desktop runtime | 10.0.15（self-contained publish）；MIT 與元件各自條款 | https://github.com/dotnet/runtime、https://github.com/dotnet/wpf；licenses/dotnet-LICENSE.txt、dotnet-THIRD-PARTY-NOTICES.txt、WPF-LICENSE.txt、WPF-NOTICES.txt |
| Jackcess／Apache Commons | 僅產生合成測試資料，不進主程式 | Jackcess 4.0.5、Commons Lang 3.10、Commons Logging 1.2，均 Apache-2.0 |

MDBTools 與 libiconv 的完整相符原始碼隨包位於 `tools/mdbtools/sources`，修補及完整重建命令位於 `tools/mdbtools/build`，來源 URL／SHA256／簽章結果與編譯選項見 docs/MDBTOOLS-WINDOWS.md。沒有只用上游連結取代相符來源；無額外非系統 DLL。GPL／LGPL 工具可依各自條款修改、重建、替換與再散布；主程式 MIT 不限制這些權利。
