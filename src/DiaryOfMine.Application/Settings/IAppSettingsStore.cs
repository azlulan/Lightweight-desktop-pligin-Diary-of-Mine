namespace DiaryOfMine.Application.Settings;

public interface IAppSettingsStore
{
    AppSettings LoadOrCreate();
    AppSettings Save(AppSettings settings);
}
