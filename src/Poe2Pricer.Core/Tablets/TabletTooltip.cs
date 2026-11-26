using Poe2Pricer.Core.Market;
using System.Text.RegularExpressions;
using Poe2Pricer.Core.Waystones;

namespace Poe2Pricer.Core.Tablets;

// What a tablet's tooltip says, read from its OCR lines: the name and kind from the title,
// the uses left and the item level (Alt held) from the head block, the mods matched against
// the kind's list. Unread: lines between the head and the flavour text that matched nothing.
public sealed record TabletReading(string Name, TabletKind Kind, int? Uses, int? ItemLevel, IReadOnlyList<TabletModReading> Mods, IReadOnlyList<string> Unread, bool Ended = true, bool Unidentified = false)
{
    // Something of the tooltip was understood (WaystoneReading.Understood): the uses, a mod,
    // or the closing line.
    public bool Understood => Ended || Uses is not null || Mods.Count > 0;

    // The values a price is asked with.
    public bool SameValues(TabletReading other) =>
        Kind == other.Kind && Uses == other.Uses
        && Mods.Select(m => (m.Mod.Text, m.Value)).OrderBy(m => m.Text).SequenceEqual(other.Mods.Select(m => (m.Mod.Text, m.Value)).OrderBy(m => m.Text));

    public TabletReading Merge(TabletReading later)
    {
        var mods = ModRows.Merged(Mods, later.Mods, m => m.Mod.Text, m => m.Value is not null);
        return this with { Uses = Uses ?? later.Uses, ItemLevel = ItemLevel ?? later.ItemLevel, Mods = mods, Unread = later.Unread, Ended = Ended || later.Ended, Unidentified = Unidentified || later.Unidentified };
    }
}

public sealed record TabletModReading(TabletMod Mod, int? Value, string Line, int Distance);

public static partial class TabletTooltip
{
    public static bool IsTitle(string line) => TabletMods.IsTitle(line);

    // lines: the tooltip's text lines from top to bottom. null: no tablet title among them.
    // The title is the first line that names a kind: the second title line of a rare tablet
    // (the name stands above it), the one line of a magic tablet (the name itself). Then the
    // "Tablet" or "Tablet: Item Level 81" line, the kind's implicit, "# uses remaining", the
    // mods, and the flavour text, where reading stops. A line without scaling ends on
    // "— Unscalable Value", which wraps; the tail goes before matching.
    public static TabletReading? Parse(IReadOnlyList<string> lines)
    {
        int title = -1;
        TabletKind? kind = null;
        // A title in two pieces counts as well, after every line alone (WaystoneTooltip.Parse).
        for (int i = 0; i < lines.Count && title < 0; i++)
            if (TabletMods.KindOf(lines[i]) is { } k)
                (title, kind) = (i, k);
        for (int i = 0; i + 1 < lines.Count && title < 0; i++)
            if (TabletMods.KindOf(lines[i] + " " + lines[i + 1]) is { } joined)
                (title, kind) = (i + 1, joined);
        if (kind is null)
            return null;
        string titleLine = lines[title].Trim();
        string titleKey = WaystoneMods.KeyOf(titleLine);
        bool baseOnly = titleKey == WaystoneMods.KeyOf(kind.Name) || (kind.De is { } de && titleKey == WaystoneMods.KeyOf(de));
        string name = baseOnly && title > 0 ? lines[title - 1].Trim() : titleLine;
        int? uses = null, itemLevel = null;
        var mods = new List<TabletModReading>();
        var unread = new List<string>();
        bool ended = false, unidentified = false;
        string implicitKey = WaystoneMods.KeyOf(kind.Implicit[0]), implicitKeyDe = WaystoneMods.KeyOf((kind.ImplicitDe ?? kind.Implicit)[0]);
        bool afterImplicit = false;
        foreach (var raw in lines.Skip(title + 1))
        {
            string line = Unscalable().Replace(Affix().Replace(raw, ""), "").Trim();
            if (line.Length == 0)
                continue;
            string key = WaystoneMods.KeyOf(line);
            // The OCR now and then drops the words of "10 uses remaining" and leaves the
            // number: a bare number up to 10 right under the implicit line is the uses.
            if (afterImplicit && uses is null && BareNumber().Match(line) is { Success: true } bare && int.Parse(bare.Groups[1].Value) is var n and >= 1 and <= 10)
            {
                uses = n;
                afterImplicit = false;
                continue;
            }
            afterImplicit = false;
            // "Unidentified" (WaystoneReading.Unidentified): not asked for.
            if (WaystoneTooltip.IsUnidentified(key))
            {
                unidentified = true;
                continue;
            }
            if (key.StartsWith("canbeusedinapersonalmapdevice") || key.StartsWith("modifierstoamap")
                || key.StartsWith("kannineinem") || key.StartsWith("werdenumeinerkarte"))
            {
                ended = true;
                break;
            }
            // "Tablet: Item Level 81", German "Tafel: Gegenstandsstufe 81".
            if (key.StartsWith("tablet") || key.StartsWith("tafel"))
            {
                if (ItemLevel().Match(line) is { Success: true } level)
                    itemLevel = int.Parse(level.Groups[1].Value);
                continue;
            }
            if (UsesLeft().Match(line) is { Success: true } left && (Near(key, "usesremaining") || Near(key, "nutzungenubrig")))
            {
                uses = int.Parse(left.Groups[1].Value);
                continue;
            }
            if (Near(key, implicitKey) || Near(key, implicitKeyDe))
            {
                afterImplicit = true;
                continue;
            }
            if (key is "implicit" or "implizit" or "prefix" or "prafix" or "suffix" || key.Length < WaystoneMods.MinimumKey)
                continue;
            if (TabletMods.Match(line, kind) is { } found)
            {
                // A line with a count shows "an additional" for 1 ("Map contains # additional
                // Essences" at 1 reads as the generic prefix's text): that wording is 1.
                int? value = found.Mod.HasValue ? WaystoneMods.Plausible(WaystoneMods.ValueOf(line), found.Mod.Max, line) : found.Mod.Twin is not null ? 1 : null;
                // A mod wrapped onto two lines (WaystoneTooltip.Parse): one mod.
                if (mods.Count > 0 && mods[^1].Mod == found.Mod)
                    mods[^1] = mods[^1] with { Value = mods[^1].Value ?? value, Line = mods[^1].Line + " " + line };
                else
                    mods.Add(new(found.Mod, value, line, found.Distance));
            }
            else
                unread.Add(line);
        }
        return new(name, kind, uses, itemLevel, mods, unread, ended, unidentified);
    }

    private static bool Near(string key, string expected) =>
        Math.Abs(key.Length - expected.Length) <= WaystoneMods.Tolerance(expected.Length) && TitleReader.Distance(key, expected) <= WaystoneMods.Tolerance(expected.Length);

    [GeneratedRegex(@"^\s*(IMPLICIT|IMPLIZIT|PREFIX|SUFFIX|PR\w?FIX)\b\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Affix();

    [GeneratedRegex(@"\s*[—–-]+\s*Unscalable(\s*Value)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Unscalable();

    [GeneratedRegex(@"(\d{1,3})\s*$")]
    private static partial Regex ItemLevel();

    // The number at the line's start; the rest of the line is compared with "uses remaining"
    // on its letters.
    [GeneratedRegex(@"^\s*(\d{1,2})\b")]
    private static partial Regex UsesLeft();

    [GeneratedRegex(@"^\W*(\d{1,2})\W*$")]
    private static partial Regex BareNumber();
}
