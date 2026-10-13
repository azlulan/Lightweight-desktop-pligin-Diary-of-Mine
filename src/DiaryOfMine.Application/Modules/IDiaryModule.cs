namespace DiaryOfMine.Application.Modules;

public sealed record ModuleDescriptor(string Id, string DisplayName, string Description);

// 仅用于受信任、随应用编译的模块；该接口不提供第三方代码沙箱。
public interface IDiaryModule : IAsyncDisposable
{
    ModuleDescriptor Descriptor { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
}
