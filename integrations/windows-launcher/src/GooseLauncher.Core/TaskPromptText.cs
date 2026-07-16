using System.Text;

namespace GooseLauncher.Core;

public static class TaskPromptText
{
    public static string Build(string text, IReadOnlyList<string> files)
    {
        if (files.Count == 0) return text;

        var result = new StringBuilder(text.TrimEnd());
        result.AppendLine();
        result.AppendLine();
        result.AppendLine("User-selected files (exact paths; treat these as explicit inputs to the task):");
        for (var index = 0; index < files.Count; index++)
            result.Append(index + 1).Append(". ").AppendLine(Path.GetFullPath(files[index]));
        return result.ToString().TrimEnd();
    }
}
