using System.Security.Cryptography;
using System.Text;

namespace Echse.App;

internal static class LocalServices
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EchseLauncher");

    public static string Protect(string password) => Convert.ToBase64String(ProtectedData.Protect(
        Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string? password) => string.IsNullOrEmpty(password) ? "" :
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(password), null,
            DataProtectionScope.CurrentUser));

    public static void Log(Exception exception)
    {
        // Log only types and stack frames: network exception messages can contain private server URLs.
        var text = $"{DateTimeOffset.Now:O} {exception.GetType().FullName}{Environment.NewLine}{exception.StackTrace}{Environment.NewLine}";
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var file = Path.Combine(DataDirectory, "errors.log");
            if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024)
                File.Move(file, file + ".old", true);
            File.AppendAllText(file, text);
        }
        catch (Exception loggingError) when (loggingError is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Fehlerprotokoll nicht verfügbar: {loggingError.GetType().Name}");
        }
    }
}
