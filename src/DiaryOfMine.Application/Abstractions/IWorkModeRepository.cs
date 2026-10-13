using DiaryOfMine.Domain;

namespace DiaryOfMine.Application.Abstractions;

public interface IWorkModeRepository
{
    Task<WorkModeState> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(WorkModeState state, CancellationToken cancellationToken = default);
}
