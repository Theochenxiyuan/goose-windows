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

}

