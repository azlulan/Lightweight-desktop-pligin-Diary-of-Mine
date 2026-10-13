using System;
using DiaryOfMine.Application.Lifecycle;

namespace DiaryOfMine.Desktop.Services;

internal sealed class ApplicationActivityState : IApplicationActivityState
{
    public bool IsSleeping { get; private set; }
    public event EventHandler? StateChanged;

    public void Sleep()
    {
        if (IsSleeping) return;
        IsSleeping = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Wake()
    {
        if (!IsSleeping) return;
        IsSleeping = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
