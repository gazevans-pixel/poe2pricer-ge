using OpenCvSharp;
using Tune = Poe2Pricer.Core.Tuning.Inventory;

namespace Poe2Pricer.Core;

// The inventory panel as found: the UI it belongs to, where it stands, and the weaker of the two
// pieces that agreed on it.
public sealed record InventoryWindow(UiMode Mode, Box Panel, double Score);

// Finds the game's inventory panel (the right side of the inventory, the stash, a vendor, the map
// device) in a frame. It is the game's picture InventoryPanelUpperBackground, drawn in both UIs,
// the keyboard's at .90, the controller's stretched .935 × .965 (Tuning.Inventory; measured
// 08.11.2026 on the stash fixtures and the vendor diagnoses). The pieces compared are parts of it
// without an item on them, cut from the game files by tools/inventory_pieces.py: the statues
// left and right and the figures at the bottom. A tooltip over the panel hides one of them;
// the panel counts when two pieces point at the same origin. In pictures without the panel a
// single piece reaches .72 (a frame corner elsewhere), two never agree.
// Why it is wanted: a waystone or tablet is priced only while this panel stands (OFFEN U23).
public sealed class InventoryPanel : IDisposable
{
    private readonly Dictionary<UiMode, (string Name, Mat Drawn, Point Offset)[]> drawn = [];

    public InventoryPanel(string uiDirectory)
    {
        var pieces = Tune.Pieces.Select(p =>
        {
            var m = Cv2.ImRead(Path.Combine(uiDirectory, $"inventory-{p.Name}.png"), ImreadModes.Grayscale);
            return m.Empty() ? throw Texts.Failure(e => new InvalidDataException(e), "MissingFile", $"inventory-{p.Name}.png") : (p.Name, p.X, p.Y, p.Only, p.Native, Piece: m);
        }).ToArray();
        foreach (var (mode, sx, sy) in new[] { (UiMode.KeyboardMouse, Tune.KeyboardScale, Tune.KeyboardScale), (UiMode.Controller, Tune.ControllerScaleX, Tune.ControllerScaleY) })
            drawn[mode] = pieces.Where(p => p.Only is null || p.Only == mode).Select(p =>
            {
                var d = new Mat();
                if (p.Native)
                    p.Piece.CopyTo(d);
                else
                    Cv2.Resize(p.Piece, d, new Size((int)Math.Round(p.Piece.Width * sx), (int)Math.Round(p.Piece.Height * sy)), 0, 0, InterpolationFlags.Area);
                return (p.Name, d, p.Native ? new Point(p.X, p.Y) : new Point((int)Math.Round(p.X * sx), (int)Math.Round(p.Y * sy)));
            }).ToArray();
        foreach (var p in pieces)
            p.Piece.Dispose();
    }

    // The panel in this frame (at the reference height; GameView scales other heights), or
    // null. Each piece is looked for in the right part of the frame and in the rows where it
    // lies when the panel's top is between OriginYFrom and OriginYTo.
    public InventoryWindow? Locate(Mat bgr)
    {
        int left = (int)(bgr.Width * Tune.SearchFrom);
        using var gray = new Mat();
        using (var roi = new Mat(bgr, new Rect(left, 0, bgr.Width - left, bgr.Height)))
            Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
        InventoryWindow? best = null;
        foreach (var (mode, set) in drawn)
        {
            var hits = new List<(double Score, Point Origin)>();
            foreach (var (_, piece, offset) in set)
            {
                var band = new Rect(0, offset.Y + Tune.OriginYFrom, gray.Width, Tune.OriginYTo - Tune.OriginYFrom + piece.Height).Intersect(new Rect(0, 0, gray.Width, gray.Height));
                if (piece.Width > band.Width || piece.Height > band.Height)
                    continue;
                using var part = new Mat(gray, band);
                using var result = new Mat();
                Cv2.MatchTemplate(part, piece, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out double max, out _, out var at);
                if (max >= Tune.PieceFrom)
                    hits.Add((max, new Point(left + at.X - offset.X, band.Y + at.Y - offset.Y)));
            }
            // The best pair that agrees: ranked by its weaker piece.
            for (int i = 0; i < hits.Count; i++)
            for (int j = i + 1; j < hits.Count; j++)
            {
                var (a, b) = hits[i].Score >= hits[j].Score ? (hits[i], hits[j]) : (hits[j], hits[i]);
                if (Math.Abs(a.Origin.X - b.Origin.X) > Tune.OriginSlack || Math.Abs(a.Origin.Y - b.Origin.Y) > Tune.OriginSlack)
                    continue;
                if (best is not null && b.Score <= best.Score)
                    continue;
                var (sx, sy) = mode == UiMode.Controller ? (Tune.ControllerScaleX, Tune.ControllerScaleY) : (Tune.KeyboardScale, Tune.KeyboardScale);
                best = new(mode, new Box(a.Origin.X, a.Origin.Y, (int)Math.Round(Tune.ArtWidth * sx), (int)Math.Round(Tune.ArtHeight * sy)), b.Score);
            }
        }
        return best;
    }

    public void Dispose()
    {
        foreach (var set in drawn.Values)
            foreach (var (_, piece, _) in set)
                piece.Dispose();
    }
}
