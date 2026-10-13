namespace DiaryOfMine.Infrastructure.Storage;

public sealed class LocalDataPaths
{
    public string ApplicationRoot { get; }
    public string UserDataRoot => Path.Combine(ApplicationRoot, "userdata");
    public string SettingsFile => Path.Combine(UserDataRoot, "appsettings.json");
    public string ReminderAcknowledgements => Path.Combine(UserDataRoot, "reminder-acknowledgements.json");
    public string DefaultCacheDirectory => Path.Combine(UserDataRoot, "cache");
    public string DefaultLogDirectory => Path.Combine(UserDataRoot, "logs");
    public string VaultDirectory => Path.Combine(UserDataRoot, "vault");
    public string Database => Path.Combine(VaultDirectory, "diary.db");
    public string CalendarData => Path.Combine(VaultDirectory, "calendar.dat");
    public string Backups => Path.Combine(UserDataRoot, "backups");

    public LocalDataPaths(bool development)
    {
        ApplicationRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            development ? "DiaryOfMine.Dev" : "DiaryOfMine");
    }
}
