using Avalonia.Media;
using Fjord.Config;

namespace Fjord.App.Views;

/// <summary>
/// Colours, typeface and metrics for <see cref="FileListView"/>.
/// </summary>
/// <remarks>
/// Every brush the list draws is resolved here, so config-driven theming has exactly one
/// place to change. Brushes are immutable instances built once per config load rather than
/// resolved per row: a lookup 40 times a frame is avoidable work in the paint loop.
/// </remarks>
public sealed class ListTheme
{
    private double _scaling = 1.0;

    public ListTheme(AppearanceConfig? appearance = null) => Apply(appearance ?? new AppearanceConfig());

    /// <summary>Bumped whenever anything cached from this theme becomes stale.</summary>
    public int Generation { get; private set; }

    public Typeface Typeface { get; private set; } = Typeface.Default;

    public double FontSize { get; private set; } = 13;

    public double RowPadding { get; private set; } = 4;

    /// <summary>Row height in DIPs, snapped to a whole device pixel.</summary>
    public double RowHeight { get; private set; }

    /// <summary>Distance from the top of a row to where text is drawn.</summary>
    public double TextOffsetY { get; private set; }

    public double LeftPadding => 8;

    public double ColumnGap => 12;

    // Column widths scale with the font, or raising font_size silently truncates the
    // columns to "2026-08-03 2…" instead of widening them.
    public double SizeColumnWidth => Math.Ceiling(FontSize * 5.5);

    /// <summary>
    /// Width of the note column, in listings that have notes.
    /// </summary>
    /// <remarks>
    /// Taken out of the name's share rather than out of the size and date, which were being
    /// dropped entirely to make room: a scrapbook entry's size and age are exactly what you
    /// are looking at it for. Notes are short by nature — the whole point is a reminder, not
    /// a paragraph — so a fixed column trims gracefully.
    /// </remarks>
    public double NoteColumnWidth => Math.Ceiling(FontSize * 14.0);

    public double ModifiedColumnWidth => Math.Ceiling(FontSize * 9.0);

    /// <summary>Width of the accent bar marking the cursor row.</summary>
    public double CursorBarWidth => 2;

    public IBrush Background { get; private set; } = Brushes.Black;

    public IBrush Stripe { get; private set; } = Brushes.Black;

    public IBrush SelectedBackground { get; private set; } = Brushes.Black;

    public IBrush VisualBackground { get; private set; } = Brushes.Black;

    public IBrush CursorBackground { get; private set; } = Brushes.Black;

    public IBrush CursorBar { get; private set; } = Brushes.White;

    public IBrush FileText { get; private set; } = Brushes.White;

    public IBrush DirectoryText { get; private set; } = Brushes.White;

    public IBrush BrokenText { get; private set; } = Brushes.White;

    public IBrush MutedText { get; private set; } = Brushes.Gray;

    /// <summary>Applies a freshly loaded appearance section.</summary>
    public void Apply(AppearanceConfig appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        Typeface = new Typeface(new FontFamily(appearance.FontFamily));
        FontSize = appearance.FontSize > 0 ? appearance.FontSize : 13;
        RowPadding = Math.Max(0, appearance.RowPadding);

        var colors = appearance.Colors;
        Background = Brush(colors.Background, 0x1B, 0x1D, 0x21);
        Stripe = Brush(colors.Stripe, 0x1F, 0x22, 0x26);
        SelectedBackground = Brush(colors.Selected, 0x2C, 0x3A, 0x4E);
        VisualBackground = Brush(colors.Visual, 0x3A, 0x33, 0x4E);
        CursorBackground = Brush(colors.Cursor, 0x2A, 0x2F, 0x37);
        CursorBar = Brush(colors.CursorBar, 0x6E, 0x9E, 0xD8);
        FileText = Brush(colors.File, 0xD4, 0xD7, 0xDD);
        DirectoryText = Brush(colors.Directory, 0x8A, 0xB4, 0xF8);
        BrokenText = Brush(colors.Broken, 0xE0, 0x7B, 0x7B);
        MutedText = Brush(colors.Muted, 0x7E, 0x86, 0x92);

        Recompute();
    }

    /// <summary>
    /// Recomputes metrics for a new render scaling.
    /// </summary>
    /// <returns>True when anything changed, meaning cached row visuals must be dropped.</returns>
    public bool SetScaling(double scaling)
    {
        if (scaling <= 0 || Math.Abs(scaling - _scaling) < 0.0001)
        {
            return false;
        }

        _scaling = scaling;
        Recompute();
        return true;
    }

    /// <summary>
    /// Parses a <c>#RRGGBB</c> setting, falling back rather than throwing.
    /// </summary>
    /// <remarks>
    /// A typo in one colour must not take the whole config down; the rest of the file is
    /// still worth honouring.
    /// </remarks>
    private static IBrush Brush(string value, byte r, byte g, byte b) =>
        new SolidColorBrush(Color.TryParse(value, out var parsed) ? parsed : Color.FromRgb(r, g, b))
            .ToImmutable();

    /// <summary>
    /// Derives row height from font metrics and snaps it to a whole device pixel.
    /// </summary>
    /// <remarks>
    /// Snapping is not cosmetic. At 150% scaling an unsnapped 21.33 DIP row puts every
    /// other baseline on a half pixel, and the list reads as blurry and unevenly spaced
    /// while scrolling.
    /// </remarks>
    private void Recompute()
    {
        var lineHeight = FontSize * 1.4;

        if (FontManager.Current.TryGetGlyphTypeface(Typeface, out var glyphTypeface))
        {
            var metrics = glyphTypeface.Metrics;
            var design = metrics.DesignEmHeight;
            if (design > 0)
            {
                var height = Math.Abs(metrics.Ascent) + Math.Abs(metrics.Descent) + Math.Abs(metrics.LineGap);
                lineHeight = height / (double)design * FontSize;
            }
        }

        var raw = lineHeight + (2 * RowPadding);
        RowHeight = Math.Ceiling(raw * _scaling) / _scaling;
        TextOffsetY = Math.Round((RowHeight - lineHeight) / 2 * _scaling) / _scaling;
        Generation++;
    }
}
