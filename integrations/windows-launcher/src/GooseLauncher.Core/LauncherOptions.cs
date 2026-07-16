using System.Diagnostics;
using System.Text.Json;

namespace GooseLauncher.Core;

public sealed record LauncherSessionSelection(
    string Provider,
    string Model,
    string? ThinkingEffort = null);

public sealed record LauncherModelOption(
    string Id,
    string Name,
    bool Reasoning,
    IReadOnlyList<string> ThinkingEfforts);

public sealed record LauncherProviderOption(
    string Id,
    string Name,
    IReadOnlyList<LauncherModelOption> Models);

public sealed record LauncherOptionsCatalog(
    int SchemaVersion,
    string? DefaultProvider,
    string? DefaultModel,
    string? DefaultThinkingEffort,
    IReadOnlyList<LauncherProviderOption> Providers)
{
    internal const int CurrentSchemaVersion = 1;

    internal static LauncherOptionsCatalog Parse(string json)
    {
        var catalog = JsonSerializer.Deserialize<LauncherOptionsCatalog>(
            json,
            DesktopActivationProtocol.JsonOptions)
            ?? throw new InvalidDataException("Goose returned an empty Launcher model catalog.");
        if (catalog.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Goose Launcher model catalog {catalog.SchemaVersion} is not supported.");
        if (catalog.Providers is null || catalog.Providers.Any(provider => provider.Models is null))
            throw new InvalidDataException("Goose returned an invalid Launcher model catalog.");
        return catalog;
    }
}

public sealed class LauncherOptionsClient
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LauncherOptionsCatalog? _cachedCatalog;
    private DateTimeOffset _cachedAt;

    public async Task<LauncherOptionsCatalog> GetAsync(
        GooseInstallation installation,
        CancellationToken cancellationToken = default)
    {
        if (_cachedCatalog is not null && DateTimeOffset.UtcNow - _cachedAt < CacheLifetime)
            return _cachedCatalog;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedCatalog is not null && DateTimeOffset.UtcNow - _cachedAt < CacheLifetime)
                return _cachedCatalog;
            _cachedCatalog = await LoadAsync(installation, cancellationToken);
            _cachedAt = DateTimeOffset.UtcNow;
            return _cachedCatalog;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<LauncherOptionsCatalog> LoadAsync(
        GooseInstallation installation,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(installation.CliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("launcher-options");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Goose to load models.");
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error) ? "Goose could not load models." : error.Trim());
            return LauncherOptionsCatalog.Parse(output);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            throw;
        }
    }
}
