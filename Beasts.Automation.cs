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

    private int _nextActionDelayMs = 300;
    private readonly Random _random = new();

    // ── Helpers ───────────────────────────────────────────────────────────────

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
    /// Returns a random click position within the center 60% of <paramref name="rect"/>.
    /// </summary>
    private Vector2 GetRandomClickPos(SharpDX.RectangleF rect)
    {
        var marginX = rect.Width  * 0.20f;
        var marginY = rect.Height * 0.20f;
        var x = rect.Left + marginX + (float)(_random.NextDouble() * (rect.Width  - marginX * 2));
        var y = rect.Top  + marginY + (float)(_random.NextDouble() * (rect.Height - marginY * 2));
        return new Vector2(x, y);
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

    // ── Click helpers ─────────────────────────────────────────────────────────

    private async SyncTask<bool> CtrlClickElement(Element element)
    {
        if (element == null) return false;

        var windowOffset = GameController.Window.GetWindowRectangleTimeCache.TopLeft.ToVector2Num();
        var clickPos = GetRandomClickPos(element.GetClientRect()) + windowOffset;

        bool ok = Settings.Automation.InputMode.Value == "InputHumanizer"
            ? await CtrlClickViaHumanizer(clickPos)
            : await CtrlClickSimple(clickPos);

        if (!ok) return false;

        _nextActionDelayMs = Settings.Automation.ActionDelayMs.Value;
        _sinceLastClick.Restart();
        return true;
    }

    private async SyncTask<bool> CtrlClickSimple(Vector2 clickPos)
    {
        Input.SetCursorPos(clickPos);
        await WaitMs(Settings.Automation.Delays.PreClickDelayMs.Value);

        Input.KeyDown(Keys.ControlKey);
        await TaskUtils.NextFrame();
        Input.Click(MouseButtons.Left);
        await TaskUtils.NextFrame();
        Input.KeyUp(Keys.ControlKey);
        return true;
    }

    private async SyncTask<bool> CtrlClickViaHumanizer(Vector2 clickPos)
    {
        var getController = GameController.PluginBridge
            .GetMethod<Func<string, TimeSpan, SyncTask<object>>>("InputHumanizer.GetInputController");

        if (getController == null)
        {
            LogError("InputHumanizer plugin not available -- switch to SimpleDelay or enable InputHumanizer.");
            Settings.Automation.Enable.Value = false;
            return false;
        }

        dynamic controller = await getController("Beasts", TimeSpan.FromMilliseconds(500));
        if (controller == null)
        {
            LogError("InputHumanizer busy -- another plugin holds the input lock.");
            return false;
        }

        try
        {
            controller.KeyDown(Keys.ControlKey);
            await controller.Click(clickPos);
            await controller.KeyUp(Keys.ControlKey, true);
        }
        finally
        {
            controller.Dispose();
        }

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
                    var btn = entry.Element[0];
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
