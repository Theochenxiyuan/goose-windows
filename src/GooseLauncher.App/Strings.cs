using System.Globalization;

namespace GooseLauncher.App;

internal static class Strings
{
    internal static bool Chinese => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    internal static string Get(string chinese, string english) => Chinese ? chinese : english;
}

