using System;
using System.Diagnostics;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Helpers;
using Vector2 = System.Numerics.Vector2;

namespace Beasts;

public partial class Beasts
{
    private SyncTask<bool> _automationTask;
    private readonly Stopwatch _sinceLastClick = Stopwatch.StartNew();
    private ServerInventory _automationInventory;

    // Random delay for the NEXT action — re-rolled after every click so the
    // inter-action timing is never the same value twice in a row.
    private int _nextActionDelayMs = 300;
    private readonly Random _random = new();

    // ── Perlin noise (ported from WheresMyCraftAt) ────────────────────────────
    // Used for smooth, correlated position jitter — avoids the visible "jumpy"
    // pattern that pure Random produces when you watch the cursor on screen.

    private static readonly int[] PerlinPerm =
    [
        151,160,137, 91, 90, 15,131, 13,201, 95, 96, 53,194,233,  7,225,
        140, 36,103, 30, 69,142,  8, 99, 37,240, 21, 10, 23,190,  6,148,
        247,120,234, 75,  0, 26,197, 62, 94,252,219,203,117, 35, 11, 32,
         57,177, 33, 88,237,149, 56, 87,174, 20,125,136,171,168, 68,175,
         74,165, 71,134,139, 48, 27,166, 77,146,158,231, 83,111,229,122,
         60,211,133,230,220,105, 92, 41, 55, 46,245, 40,244,102,143, 54,
         65, 25, 63,161,  1,216, 80, 73,209, 76,132,187,208, 89, 18,169,
        200,196,135,130,116,188,159, 86,164,100,109,198,173,186,  3, 64,
         52,217,226,250,124,123,  5,202, 38,147,118,126,255, 82, 85,212,
        207,206, 59,227, 47, 16, 58, 17,182,189, 28, 42,223,183,170,213,
        119,248,152,  2, 44,154,163, 70,221,153,101,155,167, 43,172,  9,
        129, 22, 39,253, 19, 98,108,110, 79,113,224,232,178,185,112,104,
        218,246, 97,228,251, 34,242,193,238,210,144, 12,191,179,162,241,
         81, 51,145,235,249, 14,239,107, 49,192,214, 31,181,199,106,157,
        184, 84,204,176,115,121, 50, 45,127,  4,150,254,138,236,205, 93,
        222,114, 67, 29, 24, 72,243,141,128,195, 78, 66,215, 61,156,180,
        // doubled for wrap-around
        151,160,137, 91, 90, 15,131, 13,201, 95, 96, 53,194,233,  7,225,
        140, 36,103, 30, 69,142,  8, 99, 37,240, 21, 10, 23,190,  6,148,
        247,120,234, 75,  0, 26,197, 62, 94,252,219,203,117, 35, 11, 32,
         57,177, 33, 88,237,149, 56, 87,174, 20,125,136,171,168, 68,175,
         74,165, 71,134,139, 48, 27,166, 77,146,158,231, 83,111,229,122,
         60,211,133,230,220,105, 92, 41, 55, 46,245, 40,244,102,143, 54,
         65, 25, 63,161,  1,216, 80, 73,209, 76,132,187,208, 89, 18,169,
        200,196,135,130,116,188,159, 86,164,100,109,198,173,186,  3, 64,
         52,217,226,250,124,123,  5,202, 38,147,118,126,255, 82, 85,212,
        207,206, 59,227, 47, 16, 58, 17,182,189, 28, 42,223,183,170,213,
        119,248,152,  2, 44,154,163, 70,221,153,101,155,167, 43,172,  9,
        129, 22, 39,253, 19, 98,108,110, 79,113,224,232,178,185,112,104,
        218,246, 97,228,251, 34,242,193,238,210,144, 12,191,179,162,241,
         81, 51,145,235,249, 14,239,107, 49,192,214, 31,181,199,106,157,
        184, 84,204,176,115,121, 50, 45,127,  4,150,254,138,236,205, 93,
        222,114, 67, 29, 24, 72,243,141,128,195, 78, 66,215, 61,156,180
    ];

    private float _perlinTime;

    private float PerlinNoise(float x, float y)
    {
        var X = (int)Math.Floor(x) & 255;
        var Y = (int)Math.Floor(y) & 255;
        x -= (float)Math.Floor(x);
        y -= (float)Math.Floor(y);
        var u = x * x * x * (x * (x * 6 - 15) + 10);
        var v = y * y * y * (y * (y * 6 - 15) + 10);
        var A  = PerlinPerm[X]     + Y;
        var AA = PerlinPerm[A]     % 256;
        var AB = PerlinPerm[A + 1] % 256;
        var B  = PerlinPerm[X + 1] + Y;
        var BA = PerlinPerm[B]     % 256;
        var BB = PerlinPerm[B + 1] % 256;

        static float Grad(int h, float gx, float gy)
        {
            var gu = (h & 8) == 0 ? gx : gy;
            var gv = (h & 4) == 0 ? gy : (h is 12 or 14 ? gx : 0f);
            return ((h & 1) == 0 ? gu : -gu) + ((h & 2) == 0 ? gv : -gv);
        }

        return Lerp(v,
            Lerp(u, Grad(PerlinPerm[AA], x, y),     Grad(PerlinPerm[BA], x - 1, y)),
            Lerp(u, Grad(PerlinPerm[AB], x, y - 1), Grad(PerlinPerm[BB], x - 1, y - 1)));

        static float Lerp(float t, float a, float b) => a + t * (b - a);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private int RandomInRange(Vector2 range) =>
        _random.Next((int)range.X, Math.Max((int)range.X + 1, (int)range.Y + 1));

    /// <summary>
    /// Yields frames until <paramref name="ms"/> milliseconds have passed.
    /// More accurate than counting frames; does not block the render thread.
    /// </summary>
    private static async SyncTask<bool> WaitMs(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
            await TaskUtils.NextFrame();
        return true;
    }

    /// <summary>
    /// Polls until the beast element disappears from the UI, confirming server
    /// processed the release/itemize. Uses <c>IsVisible</c> (single memory read)
    /// rather than <c>GetClientRect</c> for minimal overhead per frame.
    /// Times out after <paramref name="timeoutMs"/> ms and proceeds anyway.
    /// </summary>
    private static async SyncTask<bool> WaitForBeastProcessed(Element beastElement, int timeoutMs = 600)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                if (!beastElement.IsVisible) return true;
            }
            catch { return true; } // address invalidated — element is gone
            await TaskUtils.NextFrame();
        }
        return true;
    }

    /// <summary>
    /// Returns a click position within the center 40 % of <paramref name="rect"/>
    /// with a small Perlin-driven nudge.
    /// Tight spread reduces visible cursor distance between consecutive button clicks
    /// while still avoiding perfectly identical positions on every action.
    /// </summary>
    private Vector2 GetHumanClickPos(SharpDX.RectangleF rect)
    {
        var jitter = Settings.Automation.Delays.ClickJitter.Value;

        // Advance Perlin time by a small random step for smooth, non-periodic variation.
        _perlinTime += _random.Next(1, 6);
        if (_perlinTime > 10000f) _perlinTime = 0f;

        var noise = PerlinNoise(_perlinTime / 100f, 0f); // [-1, 1]

        // Shrink to the center 40 % of the rect (30 % margin each side).
        // Tighter area = less cursor travel between buttons, less "jumpy" appearance.
        var shrinkX = rect.Width  * 0.30f;
        var shrinkY = rect.Height * 0.30f;
        var inner = new SharpDX.RectangleF(
            rect.Left + shrinkX, rect.Top  + shrinkY,
            rect.Width  - shrinkX * 2,
            rect.Height - shrinkY * 2);

        var baseX = inner.Left + (float)(_random.NextDouble() * inner.Width);
        var baseY = inner.Top  + (float)(_random.NextDouble() * inner.Height);

        // Perlin nudge capped to half the jitter value so clicks stay tight.
        var nx = baseX + noise * (jitter * 0.5f);
        var ny = baseY + noise * (jitter * 0.35f);

        return new Vector2(nx, ny);
    }

    // ── Inventory space check ─────────────────────────────────────────────────

    /// <summary>Returns true if at least one free 1×1 slot exists in the player inventory.</summary>
    private bool HasInventorySpace()
    {
        var inv = _automationInventory;
        if (inv == null) return false;

        var rows = inv.Rows;
        var cols = inv.Columns;
        if (rows <= 0 || cols <= 0) return false;

        var grid = new bool[rows, cols];
        try
        {
            foreach (var item in inv.InventorySlotItems)
            {
                var endY = Math.Min(rows, item.PosY + item.SizeY);
                var endX = Math.Min(cols, item.PosX + item.SizeX);
                for (var y = Math.Max(0, item.PosY); y < endY; y++)
                for (var x = Math.Max(0, item.PosX); x < endX; x++)
                    grid[y, x] = true;
            }
        }
        catch { return false; }

        for (var y = 0; y < rows; y++)
        for (var x = 0; x < cols; x++)
            if (!grid[y, x]) return true;

        return false;
    }

    // ── Click helper ──────────────────────────────────────────────────────────

    private async SyncTask<bool> CtrlClickElement(Element element)
    {
        if (element == null) return false;

        var windowOffset = GameController.Window.GetWindowRectangleTimeCache.TopLeft.ToVector2Num();

        // 1. Move cursor directly to a human-like position within the button rect.
        //    Mouse is NOT reset to the beast center first — it travels naturally
        //    from wherever the last click left it.
        var clickPos = GetHumanClickPos(element.GetClientRect()) + windowOffset;
        Input.SetCursorPos(clickPos);

        // 2. Wait a random "mouse-settle + human reaction" delay before pressing.
        //    This simulates the time between cursor arriving and finger pressing —
        //    equivalent to WMCA's MinMaxRandomDelayMS polling loop.
        var preDelay = RandomInRange(Settings.Automation.Delays.MinMaxPreClickDelayMs.Value);
        await WaitMs(preDelay);

        // 3. Hold Ctrl, click, release — keep frame gaps between each input event.
        Input.KeyDown(Keys.ControlKey);
        await TaskUtils.NextFrame();
        Input.Click(MouseButtons.Left);
        await TaskUtils.NextFrame();
        Input.KeyUp(Keys.ControlKey);

        // 4. Use configured action delay and restart the cooldown timer.
        _nextActionDelayMs = Settings.Automation.ActionDelayMs.Value;
        _sinceLastClick.Restart();
        return true;
    }

    // ── Main automation loop ──────────────────────────────────────────────────

    private async SyncTask<bool> RunAutomationAsync()
    {
        if (!GameController.Window.IsForeground()) return true;
        if (!Settings.Enable.Value) return true;

        // Gate on the freshly-rolled random delay (re-rolled after each click).
        if (_sinceLastClick.ElapsedMilliseconds < _nextActionDelayMs)
            return true;

        // Use the render-layer cache — avoids re-reading beast addresses from memory.
        if (!_bestiaryVisible || _cachedBeasts.Count == 0) return true;

        var cfg       = Settings.Automation;
        int threshold = cfg.ItemizeAboveChaos.Value;
        var hasSpace  = HasInventorySpace();

        // If inventory is full, stop — nothing to do until the player makes room.
        if (cfg.CheckInventoryBeforeItemize.Value && !hasSpace)
        {
            Settings.Automation.Enable.Value = false;
            return true;
        }

        // Only interact with beasts inside the visible scroll area of the panel.
        var viewTop    = _cachedPanelRect.Top;
        var viewBottom = _cachedPanelRect.Bottom;

        foreach (var entry in _cachedBeasts)
        {
            try
            {
                var rect = entry.Element.GetClientRect();
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                if (rect.Bottom < viewTop || rect.Top > viewBottom) continue;

                // Yellow beasts (not in poe.ninja price list) are handled by their
                // own toggle — independent of the chaos threshold.
                bool shouldItemize;
                if (entry.IsGenericYellow)
                    shouldItemize = cfg.ItemizeYellowBeasts.Value;
                else
                    shouldItemize = entry.Price >= threshold;

                if (shouldItemize)
                {
                    var btn = entry.Element.ItemizeButton;
                    if (btn == null) continue;

                    await CtrlClickElement(btn);
                    await WaitForBeastProcessed(entry.Element);
                    return true;
                }
                else
                {
                    var btn = entry.Element.ReleaseButton;
                    if (btn == null) continue;

                    await CtrlClickElement(btn);
                    await WaitForBeastProcessed(entry.Element);
                    return true;
                }
            }
            catch { /* element read failure — skip beast */ }
        }

        return true;
    }
}
