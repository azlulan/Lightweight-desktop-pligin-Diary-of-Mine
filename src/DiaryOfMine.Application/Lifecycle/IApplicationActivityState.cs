namespace DiaryOfMine.Application.Lifecycle;

public interface IApplicationActivityState
{
    bool IsSleeping { get; }
    event EventHandler? StateChanged;
}
