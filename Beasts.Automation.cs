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

    private int _nextActionDelayMs;
    private readonly Random _random = new();

    /// <summary>
    /// Checks whether a beast should be itemized (true) or released (false)
    /// based on the current automation settings and price threshold.
    /// </summary>
    private bool ShouldItemizeBeast(CachedBeastEntry entry, int threshold)
    {
        if (entry.IsGenericYellow)
            return Settings.Automation.ItemizeYellowBeasts.Value;
        return entry.Price >= threshold;
    }

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
    /// Polls until the beast list count decreases, confirming the server processed the
    /// release/itemize (WTC -- Wait To Confirm). Refreshes the beast cache each frame
    /// so the caller gets fresh data on success. <c>IsVisible</c> on individual beast
    /// elements does NOT flip when the game removes them -- count-based detection is
    /// the only reliable method.
    /// Returns <c>true</c> if count decreased (confirmed), <c>false</c> on timeout.
    /// </summary>
    private async SyncTask<bool> WaitForBeastCountChange(int countBefore, int timeoutMs = 600)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await TaskUtils.NextFrame();
            RefreshBeastCache();
            _beastCacheTimer.Restart();
            if (_cachedBeasts.Count < countBefore) return true;
        }
        return false; // timed out -- server did not confirm in time
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

        bool ok = Settings.Automation.UseInputHumanizer.Value
            ? await CtrlClickViaHumanizer(clickPos)
            : await CtrlClickSimple(clickPos);

        if (!ok) return false;

        _sinceLastClick.Restart();
        return true;
    }

    private async SyncTask<bool> CtrlClickSimple(Vector2 clickPos)
    {
        Input.SetCursorPos(clickPos);
        await WaitMs(Settings.Automation.PreClickDelayMs.Value);

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
        var cfg = Settings.Automation;
        var loopSw = Stopwatch.StartNew();

        // Outer loop: after each confirmed beast, refresh the cache and immediately
        // look for the next one -- no task restart overhead or stale-cache delay.
        while (true)
        {
            if (!GameController.Window.IsForeground()) return true;
            if (!Settings.Enable.Value) return true;

            // WTC fallback: only delays if the previous action timed out without server confirmation.
            if (_sinceLastClick.ElapsedMilliseconds < _nextActionDelayMs)
                return true;

            // Use the render-layer cache -- avoids re-reading beast addresses from memory.
            if (!_bestiaryVisible || _cachedBeasts.Count == 0) return true;

            int threshold = cfg.ItemizeAboveChaos.Value;

            // If inventory is full, stop -- nothing to do until the player makes room.
            if (cfg.CheckInventoryBeforeItemize.Value && !HasInventorySpace())
            {
                Settings.Automation.Enable.Value = false;
                return true;
            }

            // Only interact with beasts inside the visible scroll area of the panel.
            var viewTop    = _cachedPanelRect.Top;
            var viewBottom = _cachedPanelRect.Bottom;

            // ── Look-ahead: count releases before next itemize target ─────────
            // -1 = no itemize target visible (all releases -- full speed)
            //  0 = first visible beast IS the itemize target (CAREFUL)
            //  1-2 = approaching an itemize target (SLOW)
            //  3+ = far away (FAST)
            int releasesBeforeItemize = -1;
            foreach (var scan in _cachedBeasts)
            {
                try
                {
                    var scanRect = scan.Element.GetClientRect();
                    if (scanRect.Width <= 0 || scanRect.Height <= 0) continue;
                    if (scanRect.Bottom < viewTop || scanRect.Top > viewBottom) continue;

                    if (ShouldItemizeBeast(scan, threshold))
                    {
                        if (releasesBeforeItemize < 0) releasesBeforeItemize = 0;
                        break;
                    }
                    releasesBeforeItemize = releasesBeforeItemize < 0 ? 1 : releasesBeforeItemize + 1;
                }
                catch { }
            }

            // ── Process the first visible beast ───────────────────────────────
            bool clickedAny = false;
            var countBefore = _cachedBeasts.Count;

            foreach (var entry in _cachedBeasts)
            {
                try
                {
                    var rect = entry.Element.GetClientRect();
                    if (rect.Width <= 0 || rect.Height <= 0) continue;
                    if (rect.Bottom < viewTop || rect.Top > viewBottom) continue;

                    bool shouldItemize = ShouldItemizeBeast(entry, threshold);

                    // CAREFUL zone: before clicking an itemize target, verify the
                    // element still matches what we cached. If the UI shifted and
                    // this address now points to a different beast, re-cache instead
                    // of risking a misclick on a valuable beast.
                    if (shouldItemize)
                    {
                        var liveName = entry.Element.Name?.Replace("-", "").Trim();
                        if (liveName != entry.DisplayName)
                        {
                            RefreshBeastCache();
                            _beastCacheTimer.Restart();
                            clickedAny = true; // force while loop restart
                            break;
                        }
                    }

                    var btn = shouldItemize ? entry.Element[0] : entry.Element.ReleaseButton;
                    if (btn == null) continue;

                    string zone = shouldItemize ? "CAREFUL"
                        : releasesBeforeItemize >= 0 && releasesBeforeItemize <= 2
                            ? $"SLOW({releasesBeforeItemize})"
                            : "FAST";

                    loopSw.Restart();
                    await CtrlClickElement(btn);
                    var clickMs = loopSw.ElapsedMilliseconds;

                    // WTC: poll cache refresh until beast count decreases.
                    var wtcSw = Stopwatch.StartNew();
                    var confirmed = await WaitForBeastCountChange(countBefore);
                    var wtcMs = wtcSw.ElapsedMilliseconds;
                    var ping = GameController.IngameState.ServerData.Latency;

                    if (confirmed)
                    {
                        _nextActionDelayMs = 0;
                        // Cache is already refreshed inside WaitForBeastCountChange.
                        LogMsg($"[Beast] click={clickMs}ms wtc={wtcMs}ms ping={ping}ms zone={zone} cache={_cachedBeasts.Count} remaining");

                        // SLOW zone: approaching an itemize target. Pause a few frames
                        // to let the game's input hit-testing catch up with the UI shift
                        // before we click the valuable beast.
                        if (releasesBeforeItemize >= 0 && releasesBeforeItemize <= 2 && !shouldItemize)
                        {
                            await TaskUtils.NextFrame();
                            await TaskUtils.NextFrame();
                            // Re-cache after settle to get fully updated positions.
                            RefreshBeastCache();
                            _beastCacheTimer.Restart();
                        }

                        clickedAny = true;
                        break; // restart foreach with fresh _cachedBeasts
                    }
                    else
                    {
                        LogMsg($"[Beast] click={clickMs}ms wtc=TIMEOUT({wtcMs}ms) ping={ping}ms zone={zone} -- applying fallback delay");
                        _nextActionDelayMs = Settings.Automation.FallbackDelayMs.Value;
                        return true;
                    }
                }
                catch { /* element read failure -- skip beast */ }
            }

            if (!clickedAny) break; // no more actionable beasts
        }

        return true;
    }
}
