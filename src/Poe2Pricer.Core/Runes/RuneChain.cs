using System.Text.RegularExpressions;

namespace Poe2Pricer.Core.Runes;

// One remnant's contribution to the chain: the runes it passes on to the next remnant (its
// crowned slots — one, two with the atlas notable "Double or Nothing", none when a crowned slot
// is empty), and the list it was chosen from. Fixed when the list closed on a choice that could
// be told from a cancel (Esc, the gamepad's cancel button, was readable and not pressed first);
// provisional when only the row under the mouse or the cursor at the moment the list closed
// says so (the list may have been closed without a choice).
public sealed record ChainLink(IReadOnlyList<Rune> Runes, bool Fixed, string List)
{
    public bool Carries(Rune rune) => Runes.Any(r => r.Id == rune.Id);
    public bool SameAs(ChainLink other) => Fixed == other.Fixed && Runes.Select(r => r.Id).SequenceEqual(other.Runes.Select(r => r.Id));
}

// One expedition of the map: the links chosen for its explosive chain, and whether its explosives
// are all placed (Complete) — the chain is then over, the next remnant chosen belongs to a new
// expedition. A map holds one (a logbook's grand expedition) or several (a map with tablets:
// three expeditions of three or four remnants, 07.11.2026).
public sealed record ChainRow(IReadOnlyList<ChainLink> Links, bool Complete);

// The runes carried through the expeditions of this map: what every remnant chosen so far
// passes on, one row per expedition. Learned from the world (RemnantGameWindow.Closed: the
// row under the mouse or the controller's cursor once the list is closed), a row closed when
// the game's explosives counter says all are placed (Complete: ExplosivesReader), forgotten
// with the next map (Entered: Client.txt names the area and its seed; the hideout and towns
// keep it) or by hand (Reset). Any thread.
// Tracked: the maps are known (ChainTracker reads the Client.txt). Until then the chain learns
// nothing — a chain without map boundaries would show runes of the last map as carried and mark
// rows that are new, which is worse than no chain (02.11.2026). The program starts it
// untracked; tests, which know their map, tracked.
public sealed class RuneChain(bool tracked = true)
{
    private readonly object gate = new();
    private sealed class Row
    {
        public readonly List<ChainLink> Links = [];
        // Its explosives are all placed: the next link opens a new row.
        public bool Complete;
    }

    private readonly List<Row> rows = [];
    private (string Area, long Seed)? instance;
    private bool inHaven;
    private volatile bool tracked = tracked;

    public event Action? Changed;

    public bool Tracked
    {
        get => tracked;
        set => tracked = value;
    }

    // Every expedition of the map in the order begun, the open one last.
    public IReadOnlyList<ChainRow> Rows
    {
        get
        {
            lock (gate)
                return rows.Select(r => new ChainRow(r.Links.ToArray(), r.Complete)).ToArray();
        }
    }

    // The links of the open expedition: what the next pack inherits. Empty once the last
    // expedition is complete — its runes went up with its explosives.
    public IReadOnlyList<ChainLink> Links
    {
        get
        {
            lock (gate)
                return rows.Count == 0 || rows[^1].Complete ? [] : rows[^1].Links.ToArray();
        }
    }

    // The player is in the hideout or a town: the chain is kept for the way back through the
    // portal, but there is no next pack — nothing is shown of it there.
    public bool InHaven
    {
        get
        {
            lock (gate)
                return inHaven;
        }
    }

    // The map instance the player is in (Entered), null before the first area.
    public (string Area, long Seed)? Instance
    {
        get
        {
            lock (gate)
                return instance;
        }
    }

    // The player is in an expedition logbook (a "grand expedition": the map is one expedition,
    // its chain ends with the map) or in one of its caves: no expedition boundary to look for.
    public bool InLogbook
    {
        get
        {
            lock (gate)
                return instance is { } i && IsLogbook(i.Area);
        }
    }

    // The link of the open expedition that carries this rune already, the fixed one first;
    // null: not carried (a complete expedition's runes are spent).
    public ChainLink? Carrying(Rune rune)
    {
        lock (gate)
            return rows.Count == 0 || rows[^1].Complete ? null : rows[^1].Links.Where(l => l.Carries(rune)).OrderByDescending(l => l.Fixed).FirstOrDefault();
    }

    // The row under the mouse when the list closed and no cancel could be seen: provisional,
    // unless a fixed link stands for this list. Without the event: it comes as the list goes, and the
    // overlay draws the strip anew right then (OverlayEngine.Hide) — the event would have the
    // engine relabel the window that is just closing, and the glance that saw it close would
    // be dropped for the window's new identity (test of 01.11.2026 at 1200 and 1080).
    public void Guess(string list, IReadOnlyList<Rune> runes) => Put(new ChainLink(runes, false, list), replaceFixed: false, tell: false);

    // The list closed on a choice, a cancel being readable: fixed; replaces whatever this list had.
    public void Confirm(string list, IReadOnlyList<Rune> runes) => Put(new ChainLink(runes, true, list), replaceFixed: true, tell: true);

    private void Put(ChainLink link, bool replaceFixed, bool tell)
    {
        if (!tracked)
            return;
        lock (gate)
        {
            if (rows.Count == 0 || rows[^1].Complete)
                rows.Add(new Row());
            var links = rows[^1].Links;
            int at = links.FindIndex(l => l.List == link.List);
            if (at >= 0 && links[at].Fixed && !replaceFixed)
                return;
            if (at >= 0 && links[at].SameAs(link))
                return;
            if (at >= 0)
                links[at] = link;
            else
                links.Add(link);
        }
        if (tell)
            Changed?.Invoke();
    }

    // The game's explosives counter says all of this expedition's explosives are placed
    // (ExplosivesReading.AllPlaced): the open expedition is over, the next remnant chosen
    // starts a new row. True when a row was closed; nothing to close (no row, or the last one
    // closed already — the counter keeps saying "detonated" near the old site) is no change.
    public bool Complete()
    {
        lock (gate)
        {
            if (rows.Count == 0 || rows[^1].Complete)
                return false;
            rows[^1].Complete = true;
        }
        Changed?.Invoke();
        return true;
    }

    // The game generated an area (Client.txt: Generating level … area "…" with seed …). A map
    // is one instance: the same area and seed again (back through the portal) keeps the
    // chain, another instance starts a new one. The hideout and the towns keep it too: a trip
    // to sell is no new expedition. True when the chain was emptied.
    public bool Entered(string area, long seed)
    {
        bool emptied = false, changed;
        lock (gate)
        {
            bool haven = IsHaven(area);
            changed = haven != inHaven;
            inHaven = haven;
            if (!haven && instance != (area, seed))
            {
                instance = (area, seed);
                emptied = rows.Count > 0;
                rows.Clear();
            }
        }
        if (emptied || changed)
            Changed?.Invoke();
        return emptied;
    }

    // Hideouts ("HideoutFelled…"), towns ("G1_town", "C_G1_town"…) and the hideout-like
    // Kingsmarch: the areas between maps.
    internal static bool IsHaven(string area) =>
        area.Contains("Hideout", StringComparison.OrdinalIgnoreCase) || area.EndsWith("_town", StringComparison.OrdinalIgnoreCase) || area.Contains("Kingsmarch", StringComparison.OrdinalIgnoreCase);

    // The logbook areas ("ExpeditionLogBook_Tropical" and ten more, WorldAreas 07.11.2026) and
    // the caves within them ("ExpeditionSubArea_…").
    internal static bool IsLogbook(string area) =>
        area.StartsWith("ExpeditionLogBook_", StringComparison.OrdinalIgnoreCase) || area.StartsWith("ExpeditionSubArea_", StringComparison.OrdinalIgnoreCase);

    public void Reset()
    {
        lock (gate)
        {
            if (rows.Count == 0)
                return;
            rows.Clear();
        }
        Changed?.Invoke();
    }

    // The identity of a list: its first rows as read, so that the same list opened again
    // (RemnantList.Reopened) names the same remnant and a second choice replaces the first.
    public static string KeyOf(IReadOnlyList<RemnantEntry> rows) => string.Join("|", rows.Take(3).Select(r => $"{r.Count}x{r.Text}"));

    // What a row of the list passes on: the runes in its crowned tiles (a row without a crown
    // passes nothing).
    public static Rune[] Carried(RuneTile[] row) => row.Where(t => t.Crowned && t.Rune is not null).Select(t => t.Rune!).ToArray();
}

// Reads the game's Client.txt for the areas the player enters: "Generating level 80 area
// "MapAugury" with seed 3141592653". Only the lines written after the reader was made, plus
// the last such line before it (the area the player is in at the program's start), and the
// client's version from its user-agent line ("PoE poe2_production/tags/0.5.5f", written once
// per start of the game; at the reader's start taken from LatestClient.txt beside, the log of
// the current run). Nothing else of the files is read or kept; they are only read (shared
// with the game).
public sealed class ClientLog(string path)
{
    public string Path { get; } = path;
    private long position = -1;
    private static readonly Regex Generating = new("Generating level (\\d+) area \"([^\"]+)\" with seed (\\d+)", RegexOptions.Compiled);
    private static readonly Regex UserAgent = new("poe2_production/tags/(\\S+)", RegexOptions.Compiled);

    // The game's version as its log names it ("0.5.5f"), null until a user-agent line was seen.
    public string? GameVersion { get; private set; }

    public static string? Find(string? gameFolder) =>
        gameFolder is null ? null : System.IO.Path.Combine(gameFolder, "logs", "Client.txt") is var file && File.Exists(file) ? file : null;

    // The newest area line since the last poll; null when there is none or the file cannot be
    // read right now.
    public (string Area, long Seed)? Poll()
    {
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (position < 0 || position > stream.Length)
            {
                position = Math.Max(0, stream.Length - Tail);
                GameVersion ??= LatestVersion();
            }
            stream.Seek(position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, false, 4096, leaveOpen: true);
            string? last = null;
            while (reader.ReadLine() is { } line)
                if (Generating.Match(line) is { Success: true } m)
                    last = line;
                else if (ParseVersion(line) is { } version)
                    GameVersion = version;
            position = stream.Length;
            return last is null ? null : Parse(last);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static (string Area, long Seed)? Parse(string line) =>
        Generating.Match(line) is { Success: true } m && long.TryParse(m.Groups[3].Value, out long seed) ? (m.Groups[2].Value, seed) : null;

    public static string? ParseVersion(string line) => UserAgent.Match(line) is { Success: true } m ? m.Groups[1].Value : null;

    // The version in LatestClient.txt beside Client.txt (the game writes it anew at every
    // start; the user-agent line stands in its first lines), null without the file or line.
    private string? LatestVersion()
    {
        var latest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path) ?? "", "LatestClient.txt");
        if (!File.Exists(latest))
            return null;
        try
        {
            using var stream = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, false, 4096, leaveOpen: true);
            for (int i = 0; i < LatestHead && reader.ReadLine() is { } line; i++)
                if (ParseVersion(line) is { } version)
                    return version;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    // How many lines of LatestClient.txt are looked at for the version (it stands within the
    // first few dozen; measured 08.11.2026: line 4 of 300 KB).
    private const int LatestHead = 200;

    // How far back the first poll looks for the area the player is in: the file grows by a
    // few KB per area, this covers the last few.
    private const int Tail = 256 * 1024;
}
