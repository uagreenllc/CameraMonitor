using System.Text.RegularExpressions;

namespace CameraMonitor.Api.Infrastructure;

public static partial class SafeName
{
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
    private static partial Regex Pattern();

    /// <summary>True for a single URL path segment that cannot escape its directory.</summary>
    public static bool IsSafe(string segment) =>
        Pattern().IsMatch(segment) && segment is not ("." or "..") && !segment.Contains("..", StringComparison.Ordinal);
}
