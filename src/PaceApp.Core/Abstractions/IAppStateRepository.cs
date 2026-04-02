using PaceApp.Core.Models;

namespace PaceApp.Core.Abstractions;

public interface IAppStateRepository
{
    Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionSummary>> LoadSessionsAsync(CancellationToken cancellationToken = default);

    Task SaveSessionAsync(SessionSummary summary, CancellationToken cancellationToken = default);
}