using System.Text.Json;
using System.Text.Json.Serialization;

namespace GooseLauncher.Core;

public enum GooseRunTarget
{
    Desktop,
    Terminal,
}

public sealed record CompanionSettings(
    string? CliPath = null,
    string? DesktopPath = null,
    GooseRunTarget RunTarget = GooseRunTarget.Desktop);

public static class CompanionSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GooseLauncher",
        "settings.json");

    public static CompanionSettings Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<CompanionSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new CompanionSettings()
                : new CompanionSettings();
        }
        catch
        {
            return new CompanionSettings();
        }
    }

    public static void Save(CompanionSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }
}
