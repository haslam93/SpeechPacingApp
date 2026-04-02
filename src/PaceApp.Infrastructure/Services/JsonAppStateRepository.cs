using System.Text.Json;
using PaceApp.Core.Abstractions;
using PaceApp.Core.Models;

namespace PaceApp.Infrastructure.Services;

public sealed class JsonAppStateRepository : IAppStateRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string stateFilePath;

    public JsonAppStateRepository(string? rootPath = null)
    {
        var basePath = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PaceApp");

        Directory.CreateDirectory(basePath);
        stateFilePath = Path.Combine(basePath, "state.json");
    }

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(cancellationToken);
        return state.Settings.Clone();
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateCoreAsync(cancellationToken);
            state.Settings = settings.Clone();
            await SaveStateCoreAsync(state, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<SessionSummary>> LoadSessionsAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(cancellationToken);
        return state.Sessions
            .OrderByDescending(session => session.EndedAt)
            .ToList();
    }

    public async Task SaveSessionAsync(SessionSummary summary, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateCoreAsync(cancellationToken);
            state.Sessions.RemoveAll(existing => existing.SessionId == summary.SessionId);
            state.Sessions.Insert(0, summary);
            state.Sessions = state.Sessions
                .OrderByDescending(session => session.EndedAt)
                .Take(50)
                .ToList();
            await SaveStateCoreAsync(state, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PersistedState> LoadStateAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadStateCoreAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PersistedState> LoadStateCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(stateFilePath))
        {
            return new PersistedState();
        }

        await using var stream = File.OpenRead(stateFilePath);
        var state = await JsonSerializer.DeserializeAsync<PersistedState>(stream, SerializerOptions, cancellationToken);
        return state ?? new PersistedState();
    }

    private async Task SaveStateCoreAsync(PersistedState state, CancellationToken cancellationToken)
    {
        var tempPath = $"{stateFilePath}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, SerializerOptions, cancellationToken);
        }

        File.Move(tempPath, stateFilePath, true);
    }

    private sealed class PersistedState
    {
        public AppSettings Settings { get; set; } = new();

        public List<SessionSummary> Sessions { get; set; } = [];
    }
}