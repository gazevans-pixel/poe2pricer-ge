using OpenCvSharp;
using Tune = Poe2Pricer.Core.Tuning.Selection;

namespace Poe2Pricer.Core;

// Finds the item under the controller cursor. The selection draws a pale yellow frame
// around the item with a small caret "^" at the middle of its bottom edge; deferred and
// greyed-out items have frames of their own, but no caret.
public sealed class SelectionDetector : IDisposable
{
    private readonly Mat caret;

    // The cursor also shows on empty cells.
    public double Threshold { get; init; } = Tune.Threshold;

    public SelectionDetector(string uiDirectory)
    {
        using var color = Cv2.ImRead(Path.Combine(uiDirectory, "controller-caret.png"), ImreadModes.Color);
        if (color.Empty())
            throw Texts.Failure(m => new InvalidDataException(m), "MissingFile", "controller-caret.png");
        caret = PaleYellow(color);
    }

    // Best caret inside the grid: bottom-centre point of the selected item and the score.
    public (Point BottomCentre, double Score) Best(Mat bgr, FavoursGrid grid)
    {
        var area = new Rect(grid.Bounds.X - Tune.SearchSide, grid.Bounds.Y, grid.Bounds.Width + 2 * Tune.SearchSide, grid.Bounds.Height + Tune.SearchBelow)
            .Intersect(new Rect(0, 0, bgr.Width, bgr.Height));
        using var roi = new Mat(bgr, area);
        using var mask = PaleYellow(roi);
        using var scores = new Mat();
        Cv2.MatchTemplate(mask, caret, scores, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(scores, out _, out double max, out _, out Point at);
        return (new Point(area.X + at.X + caret.Width / 2, area.Y + at.Y + Tune.TipAbove), max);
    }

    public ItemMatch? Selected(Mat bgr, FavoursGrid grid, IEnumerable<ItemMatch> items)
    {
        var (p, score) = Best(bgr, grid);
        if (score < Threshold)
            return null;
        return items.FirstOrDefault(i =>
            Math.Abs(i.Bounds.Bottom - p.Y) <= Tune.MatchVertical && Math.Abs(i.Bounds.X + i.Bounds.Width / 2 - p.X) <= Tune.MatchHorizontal
        );
    }

    // The caret is near white with a pale yellow rim; the item behind it changes, so only
    // bright pixels are compared, i.e. the caret's shape. Gold (the medallion and the frame of
    // a coloured item beside the caret) is kept out by the blue floor (Tune.MinimumBlue).
    private static Mat PaleYellow(Mat bgr)
    {
        var mask = new Mat();
        Cv2.InRange(bgr, new Scalar(Tune.MinimumBlue, Tune.MinimumGreen, Tune.MinimumRed), new Scalar(255, 255, 255), mask);
        return mask;
    }

    public void Dispose() => caret.Dispose();
}
