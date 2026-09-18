using System;
using System.Collections.Generic;
using static OsrsTouch.Native;

namespace OsrsTouch {

/// <summary>
/// Works out how the touchscreen's own coordinates map onto the screen.
///
/// The panel reports in its own fixed frame, tied to the physical glass - rotating the display
/// doesn't rotate the panel. Windows applies that transform for normal apps, but we read the
/// panel directly, so upside-down or portrait use lands taps in the wrong place. Same story when
/// the touchscreen isn't the primary display.
///
/// Rather than ask anyone to configure that, we learn it: Windows' own touch-generated click
/// carries the correctly transformed screen position, and the mouse hook sees those. Pairing a
/// few of them with the raw position of the finger that caused them reveals the mapping,
/// including swapped or flipped axes.
/// </summary>
class Calibrator
{
    struct Sample { public double U, V, X, Y; }

    readonly List<Sample> samples = new List<Sample>();
    bool ready;
    bool swap;                      // true when the panel's X axis runs down the screen
    double ax, bx, ay, by;
    int badRun;

    public bool Ready { get { return ready; } }
    public bool Swapped { get { return swap; } }

    /// <summary>Panel coordinates (0..1 on each axis) to screen pixels.</summary>
    public void Map(double u, double v, out double x, out double y)
    {
        if (!ready)
        {
            // Until we've learned better, use what Windows tells us about the display: which
            // monitor the game is on and how it is rotated. That is right on its own for an
            // upright or upside-down screen; the learned fit below refines the rest.
            ScreenMap.Map(u, v, out x, out y);
            return;
        }
        x = ax * (swap ? v : u) + bx;
        y = ay * (swap ? u : v) + by;
    }

    /// <summary>How far our mapping is from where Windows says the touch actually was.</summary>
    public double ErrorAt(double u, double v, double trueX, double trueY)
    {
        double x, y;
        Map(u, v, out x, out y);
        return Math.Sqrt((x - trueX) * (x - trueX) + (y - trueY) * (y - trueY));
    }

    public void Add(double u, double v, double trueX, double trueY)
    {
        // A mapping that has started disagreeing means the screen was rotated or the window
        // moved to another display - throw it away and learn the new one.
        if (ready)
        {
            if (ErrorAt(u, v, trueX, trueY) > 100)
            {
                if (++badRun >= 3)
                {
                    Program.Log("touch mapping no longer matches (screen rotated?) - recalibrating");
                    Reset();
                }
            }
            else badRun = 0;
            if (ready) return;      // already good; nothing to learn
        }

        // Only keep samples that are spread out - a dozen taps in one spot teach us nothing.
        foreach (var s in samples)
            if (Math.Abs(s.X - trueX) < 80 && Math.Abs(s.Y - trueY) < 80) return;

        samples.Add(new Sample { U = u, V = v, X = trueX, Y = trueY });
        if (samples.Count > 16) samples.RemoveAt(0);
        if (samples.Count >= 4) Fit();
    }

    public void Reset() { ready = false; badRun = 0; samples.Clear(); }

    /// <summary>
    /// Try both axis arrangements - panel X across the screen, or panel X down it - and keep
    /// whichever explains the samples better. Flips fall out as a negative scale.
    /// </summary>
    void Fit()
    {
        double e1, e2;
        double a1x, b1x, a1y, b1y, a2x, b2x, a2y, b2y;
        e1 = FitPair(false, out a1x, out b1x, out a1y, out b1y);
        e2 = FitPair(true, out a2x, out b2x, out a2y, out b2y);
        if (double.IsNaN(e1) && double.IsNaN(e2)) return;

        bool useSwap = double.IsNaN(e1) || (!double.IsNaN(e2) && e2 < e1);
        double err = useSwap ? e2 : e1;
        if (double.IsNaN(err) || err > 60) return;      // too sloppy to trust yet

        swap = useSwap;
        ax = useSwap ? a2x : a1x; bx = useSwap ? b2x : b1x;
        ay = useSwap ? a2y : a1y; by = useSwap ? b2y : b1y;
        ready = true;
        badRun = 0;
        Program.Log("touch mapping learned from " + samples.Count + " taps"
            + (swap ? " (screen rotated 90/270" : " (landscape")
            + (ax < 0 || ay < 0 ? ", flipped)" : ")"));
    }

    /// <summary>Least-squares fit of both axes for one arrangement; returns the average error.</summary>
    double FitPair(bool sw, out double sx, out double ix, out double sy, out double iy)
    {
        if (!Line(sw, true, out sx, out ix) || !Line(sw, false, out sy, out iy))
        {
            sx = ix = sy = iy = 0;
            return double.NaN;
        }
        double total = 0;
        foreach (var s in samples)
        {
            double px = sx * (sw ? s.V : s.U) + ix;
            double py = sy * (sw ? s.U : s.V) + iy;
            total += Math.Sqrt((px - s.X) * (px - s.X) + (py - s.Y) * (py - s.Y));
        }
        return total / samples.Count;
    }

    /// <summary>Straight-line fit of screen position against one panel axis.</summary>
    bool Line(bool sw, bool forX, out double slope, out double intercept)
    {
        slope = intercept = 0;
        int n = samples.Count;
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        foreach (var s in samples)
        {
            double x = forX ? (sw ? s.V : s.U) : (sw ? s.U : s.V);
            double y = forX ? s.X : s.Y;
            sumX += x; sumY += y; sumXY += x * y; sumXX += x * x;
        }
        double denom = n * sumXX - sumX * sumX;
        if (Math.Abs(denom) < 1e-9) return false;       // all samples on one line: can't tell yet
        slope = (n * sumXY - sumX * sumY) / denom;
        intercept = (sumY - slope * sumX) / n;
        return Math.Abs(slope) > 1;                     // a near-zero scale means a bad fit
    }
}
}
