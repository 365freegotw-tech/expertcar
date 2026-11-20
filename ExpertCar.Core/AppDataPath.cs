namespace ExpertCar.Core;

public static class AppDataPath
{
    public static string DirectoryPath(string? localAppData = null)
    {
        var root = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("無法取得本機使用者資料目錄");
        return Path.Combine(root, "ExpertCar");
    }

    public static string DatabasePath(string? localAppData = null) => Path.Combine(DirectoryPath(localAppData), "expertcar.db");
}
