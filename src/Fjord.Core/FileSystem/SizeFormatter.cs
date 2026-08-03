using System.Globalization;

namespace Fjord.Core.FileSystem;

/// <summary>
/// Renders byte counts for the size column.
/// </summary>
/// <remarks>
/// Runs once per visible row per frame, so it avoids <see cref="double"/> formatting and
/// allocates a single string. Uses binary units (1 KB = 1024 bytes) and three significant
/// figures, which keeps every value inside a narrow column without a decimal point
/// wandering.
/// </remarks>
public static class SizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    /// <summary>Shown for directories and for entries whose metadata was skipped.</summary>
    public const string Unknown = "";

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return Unknown;
        }

        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        var unit = 0;
        double value = bytes;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Three significant figures: 1023 KB, 9.99 MB, 10.2 MB, 102 MB.
        var format = value switch
        {
            < 10 => "0.00",
            < 100 => "0.0",
            _ => "0",
        };

        return value.ToString(format, CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}
