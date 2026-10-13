namespace DiaryOfMine.Application.Abstractions;

// 用于通知调度器重新计算下一次唤醒；禁止每秒读取整个日程库。
public interface IReminderScheduler : IAsyncDisposable
{
    Task RescheduleAsync(CancellationToken cancellationToken);
}
