namespace DormCast.Hosting;

public static class AppLog
{
    static readonly object Gate = new();
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DormCast", "logs");
    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(Path.Combine(DirectoryPath, $"{DateTime.Today:yyyy-MM-dd}.log"),
                    $"{DateTimeOffset.Now:O} {message} {exception?.GetType().Name}: {exception?.Message}{Environment.NewLine}");
            }
        }
        catch { /* Logging must not interrupt audio or shutdown. */ }
    }
}
