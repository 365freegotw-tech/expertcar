using ExpertCar.Verification;
if (args.Length != 2) throw new ArgumentException("用法：來源合成店家資料夾 新建驗證資料夾");
await V02Workflow.RunAsync(args[0], args[1], Console.WriteLine);
