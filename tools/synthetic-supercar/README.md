# 合成舊資料匯入測試店家

本工具建立全為虛構的店家資料，用於測試匯入 SuperCar（賺錢高手）資料的流程。SuperCar、賺錢高手為其權利人之商標，本軟體與其無任何關係。預設 seed `2502026`：3,000 位客戶、3,500 輛車、25,000 張 RCARFIX 工單、20,000 筆零件、1,200 個維修項目及六組搭配零件。日期覆蓋 2018–2025，含民國與西元格式。DBF 以 cp950 寫入；MDB 由 Jackcess 4.0.5 建立。

```bash
python3 tools/synthetic-supercar/generate.py ./synthetic \
  --seed 2502026 \
  --jdk ./deps/jdk-root/usr/lib/jvm/java-21-openjdk-amd64 \
  --jars ./deps
```

`--customers`、`--vehicles`、`--orders`、`--parts`、`--services` 可縮小規模。JDK 與 jar 由執行者自行提供；輸出檔不進版控。測試用小型 MDB／DBF 夾具是 `ExpertCar.Tests/Fixtures/supercar-v02.zip`。
