import com.healthmarketscience.jackcess.*;
import java.io.File;
import java.nio.file.Path;
import java.util.Random;

/** Deterministic, invented SuperCar-shaped shop. No real personal data. */
public final class SyntheticSupercar {
  static Table table(Database db,String name,String... columns) throws Exception {
    TableBuilder b=new TableBuilder(name);
    for(String c:columns)b.addColumn(new ColumnBuilder(c,DataType.TEXT).setLength(100));
    return b.toTable(db);
  }
  static Database db(Path out,String name) throws Exception {return DatabaseBuilder.create(Database.FileFormat.V2000,out.resolve(name).toFile());}
  static String plate(int i){return String.format("T%02d-%04d",i/10000,i%10000);}
  static String order(int i){return String.format("F%07d",i);}
  static String date(int i){int year=2018+i%8,month=1+i%12,day=1+i%27;return i%3==0?String.format("%03d/%02d/%02d",year-1911,month,day):String.format("%04d-%02d-%02d",year,month,day);}
  public static void main(String[] args) throws Exception {
    Path out=Path.of(args[0]);out.toFile().mkdirs();long seed=Long.parseLong(args[1]);Random random=new Random(seed);
    int customers=args.length>2?Integer.parseInt(args[2]):3000;
    int vehicles=args.length>3?Integer.parseInt(args[3]):3500;
    int orders=args.length>4?Integer.parseInt(args[4]):25000;
    int partsCount=args.length>5?Integer.parseInt(args[5]):20000;
    int servicesCount=args.length>6?Integer.parseInt(args[6]):1200;
    try(Database base=db(out,"Base.mdb")) {
      Table insurer=table(base,"INSURE","INSURENO","INSURE","PHONE");
      for(int i=1;i<=12;i++)insurer.addRow("I"+i,"虛構保險"+i,"02-0000"+String.format("%04d",i));
      Table staff=table(base,"STAFF","STAFFNO","NAME","TITLE");for(int i=1;i<=20;i++)staff.addRow("E"+i,"王○員"+i,i%3==0?"技師":"服務專員");
      Table vendor=table(base,"SUPPLIAR","SUPPNO","SUPPLIAR","PHONE1");for(int i=1;i<=80;i++)vendor.addRow("V"+i,"虛構供應商"+i,"02-0000"+String.format("%04d",i));
      Table car=table(base,"CUSTOMER","CUST","NAME","CAR_ID","CARFACTOR2","CAR_STYLE","CAR_YEAR","CAR_COLOR","PHONE","ADDR_H","INSURENO1","SUREDATE11","SUREDATE12","VERIFY_DAT","RealVerify");
      String[] makes={"豐田","本田","馬自達","福特","日產","三菱"};String[] colors={"白","黑","銀","紅","藍"};
      for(int i=1;i<=vehicles;i++){
        int owner=1+(i-1)%customers;String name="王○明"+String.format("%04d",owner%1200);
        car.addRow("C"+owner,name,plate(i),makes[i%makes.length],"虛構車型"+random.nextInt(20),i%9==0?"201905":String.valueOf(2010+i%15),colors[i%colors.length],String.format("09%08d",owner),"虛構路"+owner+"號","I"+(1+i%12),date(i),date(i+150),i%23==0?"不明日期":date(i+40),date(i+60));
      }
      // duplicate plate and customer code with conflicting name are deliberate review cases.
      car.addRow("C1","王○明0001",plate(1),"豐田","重複車","2019","白","0900000001","虛構路","I1","","","","");
      car.addRow("C1","李○異名",plate(vehicles+1),"豐田","待核對車","2019","白","0900000001","虛構路","I1","","","","");
    }
    try(Database item=db(out,"Item.MDB")) {
      Table t=table(item,"Item","ItemNo","ItemName","Unit","Sale_A","Sell");
      for(int i=1;i<=partsCount;i++)t.addRow("P"+i,"虛構零件"+i,"個",i%97==0?"":String.valueOf(50+i%500),String.valueOf(20+i%150));
      t.addRow("P1","重複料號","個","100","20");
    }
    try(Database fix=db(out,"FixItem.MDB")) {
      String[] cols=new String[5+6*4];String[] first={"FIXITEMNO","FIXITEM","FIXCASH","FIXCOST","NETIC"};System.arraycopy(first,0,cols,0,5);
      for(int k=1;k<=6;k++){int p=5+(k-1)*4;cols[p]="ItemNo"+k;cols[p+1]="ItemName"+k;cols[p+2]="ItemQty"+k;cols[p+3]="ItemSale"+k;}
      Table t=table(fix,"FIXITEM",cols);
      for(int i=1;i<=servicesCount;i++){Object[] row=new Object[cols.length];row[0]="S"+i;row[1]="虛構維修項目"+i;row[2]="800";row[3]="200";row[4]="假資料";
        for(int k=1;k<=6;k++){int p=5+(k-1)*4;row[p]="P"+(1+(i*6+k)%partsCount);row[p+1]="虛構搭配零件"+k;row[p+2]="1";row[p+3]="100";}t.addRow(row);}
      Object[] duplicate=new Object[cols.length];duplicate[0]="S1";duplicate[1]="重複項目代碼";duplicate[2]="";duplicate[3]="";duplicate[4]="";t.addRow(duplicate);
    }
    try(Database master=db(out,"RCar089.MDB")) {
      Table t=table(master,"RCARFIX","FIX_NO","CAR_ID","DATE","REALTOTAL","TOTAL","PAYED","FIXTOTAL","ITEMTOTAL");
      for(int i=1;i<=orders;i++)t.addRow(order(i),plate(1+(i-1)%vehicles),i%211==0?"不明":date(i),"1500","1500",i%4==0?"500":"0","500","1000");
      t.addRow("DUP-1",plate(1),date(1),"100","100","0","0","0");
      t.addRow("DUP-1",plate(2),date(2),"100","100","0","0","0");
    }
    try(Database detail=db(out,"SCar089.MDB")) {
      Table t=table(detail,"SCarfix","Fix_No","Car_Id","ItemNo","ItemName","Unit","Quantity","Sale","Total");
      for(int i=1;i<=orders;i++)t.addRow(order(i),plate(1+(i-1)%vehicles),"P"+(1+i%partsCount),"虛構零件施工"+i,"個","1","1000","1000");
      t.addRow("ORPHAN-1",plate(1),"P1","孤兒明細","個","1","1","1");
    }
    try(Database detail=db(out,"Fixrpt.mdb")) {
      Table t=table(detail,"FIXRPT","Fix_No","Car_Id","FixItemNo","FixItemName","FixQuantity","FixSale","FixTotal");
      for(int i=1;i<=orders;i++)t.addRow(order(i),plate(1+(i-1)%vehicles),"S"+(1+i%servicesCount),"虛構工資施工"+i,"1","500","500");
    }
    try(Database est=db(out,"PreRCar089.MDB")) {
      Table t=table(est,"PreRCarFix","估價單號","車牌號碼","估價日期","總計","估價金額");
      for(int i=1;i<=Math.max(1,orders/10);i++)t.addRow("E"+i,plate(1+(i-1)%vehicles),date(i),"900","900");
    }
    try(Database est=db(out,"PreSCar089.MDB")) {
      Table t=table(est,"PreSCarFix","估價單號","零件代號","零件名稱","數量","單價","總計","單位");
      for(int i=1;i<=Math.max(1,orders/10);i++)t.addRow("E"+i,"P"+i,"虛構估價零件"+i,"1","900","900","個");
      t.addRow("ORPHAN-EST","P1","孤兒估價","1","100","100","個");
    }
    try(Database pay=db(out,"Cust_Pay.MDB")) {
      Table t=table(pay,"Cust_Pay","Fix_No","In_Date","Cash","CardPay","Total");
      for(int i=1;i<=orders;i++)if(i%4==0)t.addRow(order(i),date(i),"500","0","500");
      t.addRow("ORPHAN-PAY",date(1),"100","0","100");
    }
    try(Database due=db(out,"WORK.MDB")) {
      Table t=table(due,"CUSTNOTPAY","Fix_No","NotPays","Payed","RealTotals");
      for(int i=1;i<=orders;i++)if(i%4==0)t.addRow(order(i),"1000","500","1500");
      t.addRow("ORPHAN-DUE","500","0","500");
    }
    System.out.printf("seed=%d customers=%d vehicles=%d orders=%d parts=%d services=%d%n",seed,customers,vehicles,orders,partsCount,servicesCount);
  }
}
