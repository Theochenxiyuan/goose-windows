using System.Text.Json;

namespace GooseLauncher.Core;

public sealed record ActivationRequest(string Folder, int? X, int? Y, IReadOnlyList<string> Files)
{
    public const int MaxFiles = 8;

    public static ActivationRequest Create(string folder, int? x = null, int? y = null, IEnumerable<string?>? files = null)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("A working directory is required.", nameof(folder));
        var fullFolder = Path.GetFullPath(folder);
        if (!Directory.Exists(fullFolder)) throw new DirectoryNotFoundException(fullFolder);

        var validated = (files ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (validated.Length > MaxFiles) throw new InvalidDataException($"At most {MaxFiles} files can be selected.");

        foreach (var file in validated)
        {
            if (!File.Exists(file)) throw new FileNotFoundException("The selected file does not exist.", file);
            var parent = Path.GetDirectoryName(file);
            if (!string.Equals(parent?.TrimEnd(Path.DirectorySeparatorChar), fullFolder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Every selected file must share the working directory as its parent.");
        }
        return new ActivationRequest(fullFolder, x, y, validated);
    }

    public static ActivationRequest FromProtocolUri(Uri uri)
    {
        if (!uri.Scheme.Equals("goosecompanion", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported activation scheme.");
        if (!uri.Host.Equals("show", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported activation action.");

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("folder", out var folder)) throw new InvalidDataException("Activation is missing folder.");
        var x = ParseNullableInt(query.GetValueOrDefault("x"));
        var y = ParseNullableInt(query.GetValueOrDefault("y"));
        string?[] files = [];
        if (query.TryGetValue("files", out var filesJson) && !string.IsNullOrWhiteSpace(filesJson))
            files = JsonSerializer.Deserialize<string?[]>(filesJson) ?? [];
        return Create(folder, x, y, files);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            result[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return result;
    }

    private static int? ParseNullableInt(string? value) => int.TryParse(value, out var parsed) ? parsed : null;
}

