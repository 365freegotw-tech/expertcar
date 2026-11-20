import com.healthmarketscience.jackcess.*;
import java.io.File;
import java.nio.charset.Charset;

public class SyntheticMdb {
    public static void main(String[] args) throws Exception {
        String chinese = new String("測試客戶".getBytes(Charset.forName("MS950")), Charset.forName("MS950"));
        try (Database db = DatabaseBuilder.create(Database.FileFormat.V2000, new File(args[0]))) {
            Table table = new TableBuilder("CUSTOMER")
                .addColumn(new ColumnBuilder("NAME", DataType.TEXT).setLength(100))
                .addColumn(new ColumnBuilder("DATE_TEXT", DataType.TEXT).setLength(20))
                .addColumn(new ColumnBuilder("AMOUNT", DataType.TEXT).setLength(20))
                .addColumn(new ColumnBuilder("NOTE", DataType.TEXT).setLength(100))
                .addColumn(new ColumnBuilder("ADDR_H", DataType.TEXT).setLength(100))
                .addColumn(new ColumnBuilder("CAR_ID", DataType.TEXT).setLength(20))
                .addColumn(new ColumnBuilder("PHONE", DataType.TEXT).setLength(30))
                .addColumn(new ColumnBuilder("MILEAGE", DataType.TEXT).setLength(20))
                .addColumn(new ColumnBuilder("CAR_YEAR", DataType.TEXT).setLength(20))
                .addColumn(new ColumnBuilder("Next_Date1", DataType.TEXT).setLength(20))
                .addColumn(new ColumnBuilder("Next_Move1", DataType.TEXT).setLength(20))
                .addColumn(new ColumnBuilder("Next_Days1", DataType.TEXT).setLength(20))
                .addColumn(new ColumnBuilder("VERIFY_DAT", DataType.TEXT).setLength(20))
                .addColumn(new ColumnBuilder("SUREDATE11", DataType.TEXT).setLength(20))
                .toTable(db);
            table.addRow(chinese, "113/11/20", "1234.56", "含逗號,引號\"與換行\n第二行", "虛構路", "ABC-1234", "0900000001", "公里", "2024", "1130501", "15000", "180", "2024/12/01", "未證實日期");
            table.addRow("虛構乙", "", "0.25", "", "", "ABC 1234", "", "公里", "0", "0", "0", "0", "0", "");
            table.addRow("虛構丙", "2024-11-20", "100.00", "", "", "ZZ-5678", "", "公里", "", "113/02/30", "0", "0", "0", "");
            table.addRow("虛構丁", "", "0", "", "", "TT-9012", "", "公里", "0", "1130501", "0", "0", "2025-01-15", "");
        }
    }
}
