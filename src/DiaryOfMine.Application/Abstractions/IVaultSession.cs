namespace DiaryOfMine.Application.Abstractions;

// 解锁凭据的收集与验证由安全模块实现，禁止通过普通字符串配置传递密码。
// 所有存储操作必须在实现内部再次检查锁定状态，不能仅依靠 UI 检查。
public interface IVaultSession
{
    bool IsUnlocked { get; }
    event EventHandler? LockStateChanged;
    Task LockAsync(CancellationToken cancellationToken);
}
