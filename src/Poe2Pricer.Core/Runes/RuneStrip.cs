namespace Poe2Pricer.Core.Runes;

// One tile of the rune chain: the rune's picture, its class colour, hollow while provisional.
public sealed record StripEntry(string Rune, string Class, bool Fixed);

// One row of the strip: an expedition's chain — its title ("Rune chain · 4"), its tiles in
// the order chosen, and whether its explosives are all placed (Complete: drawn faint, with a
// green check after the title).
public sealed record StripRow(string Title, StripEntry[] Entries, bool Complete)
{
    public bool SameAs(StripRow other) => Title == other.Title && Complete == other.Complete && Entries.AsSpan().SequenceEqual(other.Entries);
}

// The rune chain as the overlay draws it (LabelLayer.DrawStrip): a plate at the top of the
// screen, one row per expedition of the map, each with its title above its tiles. Hover: the
// tile under the mouse (row and index; null: none), Caption: that rune's name and effect,
// drawn under the plate. The tiles' places are laid out here, in the reference picture's
// pixels, so that the engine can tell which tile the mouse is on: every row's tiles centred
// on the screen's middle, its title above them; the plate wraps whatever that spans.
public sealed record RuneStrip(StripRow[] Rows, (int Row, int Index)? Hover = null, string? Caption = null)
{
    public const int Top = 8;
    public const int Pad = 10;
    public const int Tile = 36;
    public const int Gap = 6;
    public const int TitleHeight = 20;
    public const int RowGap = 8;
    public const int RowHeight = TitleHeight + 2 + Tile;
    public const int TilesTop = Top + Pad + TitleHeight + 2;

    public int TilesWidth(int row) => Rows[row].Entries.Length * (Tile + Gap) - Gap;

    // width: the reference picture's width (Scene.Screen.Width / Scene.Scale).
    public int TilesLeft(int row, double width) => (int)Math.Round(width / 2 - TilesWidth(row) / 2.0);

    public static int RowTop(int row) => Top + Pad + row * (RowHeight + RowGap);

    public Box TileBox(int row, int index, double width) => new(TilesLeft(row, width) + index * (Tile + Gap), RowTop(row) + TitleHeight + 2, Tile, Tile);

    // The plate's bottom edge.
    public int Bottom => RowTop(Rows.Length - 1) + RowHeight + Pad;

    // The tile under the pointer, null for none.
    public (int Row, int Index)? TileAt((int X, int Y)? pointer, double width)
    {
        if (pointer is not { } p)
            return null;
        for (int r = 0; r < Rows.Length; r++)
            for (int i = 0; i < Rows[r].Entries.Length; i++)
                if (TileBox(r, i, width).Holds(p.X, p.Y))
                    return (r, i);
        return null;
    }

    public bool SameAs(RuneStrip? other) =>
        other is not null && Hover == other.Hover && Caption == other.Caption && Rows.Length == other.Rows.Length && Rows.Zip(other.Rows).All(p => p.First.SameAs(p.Second));

    // The strip of a chain: one row per expedition that carries runes, the tiles in the order
    // chosen; title: the row's title for its count of runes. Runes: the rune behind every
    // tile (row, index), for the caption.
    public static (RuneStrip Strip, Rune[][] Runes)? Of(IReadOnlyList<ChainRow> rows, Func<int, string> title)
    {
        var carried = rows.Select(r => (Runes: r.Links.SelectMany(l => l.Runes.Select(rune => (Rune: rune, l.Fixed))).ToArray(), r.Complete)).Where(r => r.Runes.Length > 0).ToArray();
        if (carried.Length == 0)
            return null;
        var strip = new RuneStrip(carried.Select(r => new StripRow(title(r.Runes.Length), r.Runes.Select(x => new StripEntry(x.Rune.Id, RuneBook.KeyOf(x.Rune.Class), x.Fixed)).ToArray(), r.Complete)).ToArray());
        return (strip, carried.Select(r => r.Runes.Select(x => x.Rune).ToArray()).ToArray());
    }

    public override string ToString() => $"{Rows.Length} rows ({string.Join("; ", Rows.Select(r => r.Title + (r.Complete ? " ✓" : "")))})/{Hover}/{Caption}";
}
