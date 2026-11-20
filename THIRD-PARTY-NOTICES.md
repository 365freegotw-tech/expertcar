# Third-party notices

本檔記錄目前直接依賴與可能另行安裝的 MDBTools。ExpertCar v0.1.0 Release 不附 MDBTools 執行檔或函式庫，因此該 Release 不需要附 MDBTools 原始碼。若後續發行包附上 MDBTools，須同時提供相符的完整原始碼、授權文字與必要建置資訊；其 GPL／LGPL 條款不受 ExpertCar 的 MIT 授權取代。

| 元件 | 版本／範圍 | 授權與來源 |
| --- | --- | --- |
| DbfDataReader | NuGet 2.2.0，DBF 讀取 | MIT；Copyright (c) 2016 Yellow Feather Ltd；[原始碼與授權](https://github.com/yellowfeather/DbfDataReader/blob/main/LICENSE) |
| MDBTools | 可另行安裝；本版不附 | 命令列工具 GPL、函式庫 LGPL；[官方授權說明](https://github.com/mdbtools/mdbtools/blob/dev/README.md)、[COPYING](https://github.com/mdbtools/mdbtools/blob/dev/COPYING)、[COPYING.LIB](https://github.com/mdbtools/mdbtools/blob/dev/COPYING.LIB)。 |
| Microsoft.Data.Sqlite / Core | NuGet 10.0.15 | MIT；Copyright (c) .NET Foundation and Contributors；[授權](https://github.com/dotnet/efcore/blob/main/LICENSE.txt) |
| SQLitePCLRaw.bundle_e_sqlite3 及相關套件 | NuGet 2.1.13，SQLite 轉接與原生庫 | Apache-2.0；Copyright SourceGear, LLC；[專案](https://github.com/ericsink/SQLitePCL.raw)。SQLite 核心為 public domain，發行時核對實際套件授權檔。 |
| Jackcess / Apache Commons Lang / Commons Logging | 僅用於重新產生合成 MDB 夾具，不進產品 | Jackcess 4.0.5 Apache-2.0；Commons Lang 3.10 Apache-2.0；Commons Logging 1.2 Apache-2.0。來源於 Maven Central，生成腳本不提交 JAR。 |

DbfDataReader 的 MIT 授權要求散布時保留上述著作權與完整許可文字；安裝包可直接收錄該 NuGet 套件的 LICENSE。MDBTools 的 GPL／LGPL 散布流程見 [Windows 打包方案](docs/MDBTOOLS-WINDOWS.md)。
