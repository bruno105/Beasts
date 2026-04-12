using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Beasts.Data;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Beasts;

public partial class Beasts
{
    private const int TileToGridConversion = 23;
    private const int TileToWorldConversion = 250;
    private const float GridToWorldMultiplier = TileToWorldConversion / (float)TileToGridConversion;
    private const double CameraAngle = 38.7 * Math.PI / 180;
    private static readonly float CameraAngleCos = (float)Math.Cos(CameraAngle);
    private static readonly float CameraAngleSin = (float)Math.Sin(CameraAngle);

    private double _mapScale;
    private SharpDX.RectangleF _rect;
    private ImDrawListPtr _backGroundWindowPtr;

    // ── Beast panel cache ────────────────────────────────────────────────────
    // CapturedBeastsPanel.CapturedBeasts allocates a new List and reads all beast
    // addresses from memory on every call. We cache it at most once per BeastCacheMs
    // and share the result across all draw methods and the automation task.

    private record CachedBeastEntry(
        CapturedBeast Element,
        string DisplayName,
        float Price,
        bool IsGenericYellow,
        bool IsSelected);

    private readonly List<CachedBeastEntry> _cachedBeasts = new();
    private readonly Stopwatch _beastCacheTimer = Stopwatch.StartNew();
    private bool _bestiaryVisible;
    private SharpDX.RectangleF _cachedPanelRect;
    private readonly HashSet<string> _selectedBeastPathsSet = new(StringComparer.Ordinal);
    private const int BeastCacheMs = 250;
    private const int SelectedPathsRefreshMs = 250;

    // ── Per-frame render snapshot ─────────────────────────────────────────────
    // Built in Tick() so Render() never calls GetComponent or ToWorldWithTerrainHeight.
    // Value-type snapshot -- no stale-entity risk between Tick and Render.
    private readonly record struct BeastRenderSnapshot(
        long EntityId,
        Vector2 GridPos,
        Vector3 WorldPos,
        string DisplayName,
        Color FillColor,
        Color OutlineColor,
        Color TextColor,
        bool IsYellow,
        bool IsSelected);

    private readonly List<BeastRenderSnapshot> _renderSnapshots = new(32);

    // ── Automation status ─────────────────────────────────────────────────────
    private enum AutomationStatus { Idle, InProgress, Completed }
    private AutomationStatus _automationStatus = AutomationStatus.Idle;
    private DateTime _automationCompletedAt = DateTime.MinValue;
    private const float AutomationCompletedShowSeconds = 5f;

    // True when any bestiary feature is active — gates the cache refresh so we
    // never traverse the UI tree just to find nothing to do.
    private bool NeedsBestiaryCache =>
        Settings.ShowBestiaryPanel.Value ||
        Settings.ShowAllPricesInBestiaryPanel.Value ||
        Settings.ShowBestiaryDebug.Value ||
        Settings.Automation.Enable.Value;

    public override void Render()
    {
        // _selectedBeastPathsSet is now rebuilt in Tick() -- no LINQ in Render.

        // Only rebuild the full bestiary panel cache (expensive UI traversal) when at
        // least one feature that needs it is active AND the left panel is open.
        if (NeedsBestiaryCache && _beastCacheTimer.ElapsedMilliseconds >= BeastCacheMs)
        {
            RefreshBeastCache();
            _beastCacheTimer.Restart();
        }
        else if (_beastCacheTimer.ElapsedMilliseconds >= BeastCacheMs)
        {
            _beastCacheTimer.Restart();
        }

        // Scoped occlusion guards -- in-world draws skip when a blocking UI is open
        // or during spawn immunity. Panel overlays (bestiary, inventory, stash) and
        // the tracker window intentionally do NOT gate on this -- they need their
        // respective panels to be visible.
        var ingameUi = GameController.Game.IngameState.IngameUi;
        var uiOcclusion =
            ingameUi.FullscreenPanels.Any(x => x.IsVisible) ||
            ingameUi.LargePanels.Any(x => x.IsVisible);
        var sidePanelOpen =
            ingameUi.OpenLeftPanel.IsVisible ||
            ingameUi.OpenRightPanel.IsVisible;
        var canDrawInWorld = !uiOcclusion && !sidePanelOpen && !_playerInGracePeriod;

        if (canDrawInWorld) DrawInGameBeasts();
        // Large map overlay draws onto the map widget itself, which is visible
        // alongside the side panels -- so it only gates on uiOcclusion, not on
        // sidePanelOpen or grace period.
        if (Settings.ShowBeastPricesOnLargeMap.Value && !uiOcclusion) DrawBeastsOnLargeMap();
        if (Settings.ShowBestiaryPanel.Value) DrawBestiaryPanel();
        if (Settings.ShowAllPricesInBestiaryPanel.Value) DrawBestiaryPrices();
        if (Settings.ShowBestiaryDebug.Value) DrawBestiaryDebug();
        if (Settings.ShowTrackedBeastsWindow.Value) DrawBeastsWindow();
        if (Settings.ShowCapturedBeastsInInventory.Value) DrawInventoryBeasts();
        if (Settings.ShowCapturedBeastsInStash.Value) DrawStashBeasts();

        if (Settings.Automation.Hotkey.PressedOnce())
            Settings.Automation.Enable.Value = !Settings.Automation.Enable.Value;

        // Auto-stop when bestiary closes while automation was running, and track
        // status for the tracker window display.
        if (_automationStatus == AutomationStatus.InProgress)
        {
            if (!_bestiaryVisible || !Settings.Automation.Enable.Value)
            {
                Settings.Automation.Enable.Value = false;
                _automationStatus = AutomationStatus.Completed;
                _automationCompletedAt = DateTime.Now;
            }
        }
        else if (Settings.Automation.Enable.Value && _bestiaryVisible)
            _automationStatus = AutomationStatus.InProgress;

        if (_automationStatus == AutomationStatus.Completed &&
            (DateTime.Now - _automationCompletedAt).TotalSeconds >= AutomationCompletedShowSeconds)
            _automationStatus = AutomationStatus.Idle;

        if (Settings.Automation.Enable.Value)
            TaskUtils.RunOrRestart(ref _automationTask, RunAutomationAsync);
        else
            _automationTask = null;
    }

    /// <summary>
    /// Rebuilds the cached beast list from the Bestiary UI panel.
    /// Calling CapturedBeastsPanel.CapturedBeasts is the most expensive operation
    /// in this plugin (allocates a new List + reads all addresses from memory).
    /// By caching here we cut it from ~180× per second (3 callers × 60 fps) to 4×/sec.
    /// DisplayName (tooltip traversal, 3+ memory reads per beast) is also cached here.
    /// </summary>
    private void RefreshBeastCache()
    {
        _cachedBeasts.Clear();
        _bestiaryVisible = false;

        // _selectedBeastPathsSet is rebuilt unconditionally in Render() — no need to
        // repeat it here. Only compute the display-name set needed for _cachedBeasts.
        var selectedDisplayNames = Settings.Beasts
            .Select(b => b.DisplayName)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.Ordinal);

        var ingameUi = GameController.IngameState.IngameUi;

        // Fast pre-check: if the left panel isn't open at all, the bestiary can't
        // be visible -- skip the expensive UI traversal entirely.
        if (!ingameUi.OpenLeftPanel.IsVisible) return;

        var bestiary = ingameUi.ChallengesPanel?.TabContainer?.BestiaryTab;
        if (bestiary == null || !bestiary.IsVisible) return;

        var cbp = bestiary.CapturedBeastsTab;
        if (cbp == null || !cbp.IsVisible) return;

        _bestiaryVisible = true;
        _cachedPanelRect = cbp.GetClientRect();

        foreach (var beast in cbp.CapturedBeasts)
        {
            try
            {
                var name = beast.Name?.Replace("-", "").Trim();
                if (string.IsNullOrEmpty(name)) continue;

                var isGenericYellow = !Settings.BeastPrices.TryGetValue(name, out var price);
                // Yellow beasts not tracked by poe.ninja → price 0, always released.

                _cachedBeasts.Add(new CachedBeastEntry(
                    beast, name, price, isGenericYellow,
                    selectedDisplayNames.Contains(name)));
            }
            catch { }
        }
    }

    // ── Large map ─────────────────────────────────────────────────────────────

    private void DrawBeastsOnLargeMap()
    {
        var ingameUi = GameController.IngameState.IngameUi;

        _rect = GameController.Window.GetWindowRectangle() with { Location = SharpDX.Vector2.Zero };
        if (ingameUi.OpenRightPanel.IsVisible)
            _rect.Right = ingameUi.OpenRightPanel.GetClientRectCache.Left;
        if (ingameUi.OpenLeftPanel.IsVisible)
            _rect.Left = ingameUi.OpenLeftPanel.GetClientRectCache.Right;

        ImGui.SetNextWindowSize(new Vector2(_rect.Width, _rect.Height));
        ImGui.SetNextWindowPos(new Vector2(_rect.Left, _rect.Top));
        ImGui.Begin("beasts_radar_background",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoBackground);

        _backGroundWindowPtr = ImGui.GetForegroundDrawList();

        var largeMap = ingameUi.Map.LargeMap.AsObject<SubMap>();
        if (largeMap.IsVisible)
        {
            _mapScale = largeMap.MapScale;
            DrawBeastsOnMap(largeMap.MapCenter);
        }

        ImGui.End();
    }

    private void DrawBeastsOnMap(Vector2 mapCenter)
    {
        var player = GameController.Game.IngameState.Data.LocalPlayer;
        var playerRender = player?.GetComponent<Render>();
        var playerPositioned = player?.GetComponent<Positioned>();
        if (playerRender == null || playerPositioned == null) return;

        var playerPosition = new Vector2(playerPositioned.GridPosNum.X, playerPositioned.GridPosNum.Y);
        var playerHeight = -playerRender.RenderStruct.Height;
        var heightData = GameController.IngameState.Data.RawTerrainHeightData;

        for (int i = 0; i < _renderSnapshots.Count; i++)
        {
            var snap = _renderSnapshots[i];
            if (!snap.IsYellow && !snap.IsSelected) continue;

            var mapPos = EntityToMapPos(snap.GridPos, playerPosition, playerHeight, heightData, mapCenter);

            string text;
            Color textColor;

            if (snap.IsYellow)
            {
                text = snap.DisplayName;
                textColor = new Color(255, 250, 0);
            }
            else
            {
                if (!Settings.BeastPrices.TryGetValue(snap.DisplayName, out var price) || price <= 0) continue;
                text = $"{price.ToString(CultureInfo.InvariantCulture)}c";
                textColor = snap.OutlineColor;
            }

            var textSize = Graphics.MeasureText(text);
            var textOffset = textSize / 2f;
            DrawBox(mapPos - textOffset - new Vector2(4, 2), mapPos + textOffset + new Vector2(4, 2), new Color(0, 0, 0, 180));
            DrawText(text, mapPos - textOffset, textColor);
        }
    }

    private Vector2 EntityToMapPos(Vector2 gridPos, Vector2 playerPosition, float playerHeight,
        float[][] heightData, Vector2 mapCenter)
    {
        float beastHeight = 0;
        var beastX = (int)gridPos.X;
        var beastY = (int)gridPos.Y;
        if (heightData != null && beastY >= 0 && beastY < heightData.Length
            && beastX >= 0 && beastX < heightData[beastY].Length)
            beastHeight = heightData[beastY][beastX];

        return mapCenter + TranslateGridDeltaToMapDelta(gridPos - playerPosition, playerHeight + beastHeight);
    }

    private Vector2 TranslateGridDeltaToMapDelta(Vector2 delta, float deltaZ)
    {
        deltaZ /= GridToWorldMultiplier;
        return (float)_mapScale * new Vector2((delta.X - delta.Y) * CameraAngleCos,
            (deltaZ - (delta.X + delta.Y)) * CameraAngleSin);
    }

    private void DrawBox(Vector2 p0, Vector2 p1, Color color)
    {
        _backGroundWindowPtr.AddRectFilled(p0, p1,
            ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(
                color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f)));
    }

    private void DrawText(string text, Vector2 pos, Color color)
    {
        _backGroundWindowPtr.AddText(pos,
            ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(
                color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f)), text);
    }

    // ── In-game world labels ──────────────────────────────────────────────────

    private void DrawInGameBeasts()
    {
        var camera = GameController.IngameState.Camera;

        for (int i = 0; i < _renderSnapshots.Count; i++)
        {
            var snap = _renderSnapshots[i];
            if (!snap.IsSelected) continue;

            // Built-in safe circle helpers -- handle frustum edge cases correctly.
            // Two-layer pattern preserves the original 20% fill + full-alpha outline look.
            Graphics.DrawFilledCircleInWorld(snap.WorldPos, 100f, snap.FillColor);
            Graphics.DrawCircleInWorld(snap.WorldPos, 100f, snap.OutlineColor, 2f, 24, false);

            // Text: gate on WorldToScreen == Vector2.Zero (ExileCore's off-screen sentinel).
            var screenPos = camera.WorldToScreen(snap.WorldPos);
            if (screenPos == Vector2.Zero) continue;

            Graphics.DrawText(snap.DisplayName, screenPos, snap.TextColor, FontAlign.Center);
        }
    }

    private static Color GetSpecialBeastColor(string beastName)
    {
        if (beastName.Contains("Vivid"))  return new Color(255, 250, 0);
        if (beastName.Contains("Wild"))   return new Color(255, 0, 235);
        if (beastName.Contains("Primal")) return new Color(0, 245, 255);
        if (beastName.Contains("Black"))  return new Color(255, 255, 255);
        return Color.Red;
    }

    // ── Bestiary panel overlays ────────────────────────────────────────────────

    private void DrawBestiaryPanel()
    {
        if (!_bestiaryVisible || _cachedBeasts.Count == 0) return;

        foreach (var entry in _cachedBeasts)
        {
            if (!entry.IsSelected) continue;
            try
            {
                var rect = entry.Element.GetClientRect();
                if (rect.Width <= 0 || rect.Height <= 0) continue;

                var center = new Vector2(rect.Center.X, rect.Center.Y);
                Graphics.DrawFrame(rect, Color.White, 2);
                Graphics.DrawText(entry.DisplayName, center, Color.White, FontAlign.Center);
                Graphics.DrawText($"{entry.Price.ToString(CultureInfo.InvariantCulture)}c",
                    center + new Vector2(0, 20), Color.White, FontAlign.Center);
            }
            catch { }
        }
    }

    private void DrawBestiaryPrices()
    {
        if (!_bestiaryVisible || _cachedBeasts.Count == 0) return;

        const float rowPadding = 40f;
        var viewTop    = _cachedPanelRect.Top    - rowPadding;
        var viewBottom = _cachedPanelRect.Bottom + rowPadding;
        var mouse      = ImGui.GetMousePos();

        foreach (var entry in _cachedBeasts)
        {
            try
            {
                var rect = entry.Element.GetClientRect();
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                // Viewport cull: skip beasts outside the visible scroll area
                if (rect.Bottom < viewTop || rect.Top > viewBottom) continue;
                // Hide badge when mouse hovers this slot (game tooltip is shown)
                if (rect.Contains(mouse.X, mouse.Y)) continue;

                var price = entry.Price;
                if (entry.IsGenericYellow && price <= 0) continue;

                string label;
                Color color;
                if      (price >= 100) { label = $"{price.ToString(CultureInfo.InvariantCulture)}c"; color = new Color(0, 255, 100); }
                else if (price >= 20)  { label = $"{price.ToString(CultureInfo.InvariantCulture)}c"; color = new Color(255, 200, 0); }
                else if (price > 0)    { label = $"{price.ToString(CultureInfo.InvariantCulture)}c"; color = entry.IsGenericYellow ? new Color(255, 210, 80) : Color.White; }
                else                   { label = "0c"; color = new Color(140, 140, 140); }

                var textSize = Graphics.MeasureText(label);
                var badgeX = rect.Center.X - textSize.X * 0.5f;
                var badgeY = rect.Bottom - textSize.Y - 3;
                var badgeRect = new SharpDX.RectangleF(badgeX - 3, badgeY - 1, textSize.X + 6, textSize.Y + 2);
                Graphics.DrawBox(badgeRect, new Color(0, 0, 0, 210));
                Graphics.DrawText(label, new Vector2(badgeX, badgeY), color, FontAlign.Left);
            }
            catch { }
        }
    }

    // ── Debug window ──────────────────────────────────────────────────────────

    private static readonly string DebugOutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bestiary_debug.txt");
    private DateTime _lastDebugWrite = DateTime.MinValue;

    private void DrawBestiaryDebug()
    {
        ImGui.SetNextWindowSize(new Vector2(520, 0), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.85f);
        ImGui.Begin("Bestiary Debug", ImGuiWindowFlags.NoCollapse);

        var ui = GameController.IngameState.IngameUi;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== Bestiary Debug {DateTime.Now:HH:mm:ss} ===");

        var challengesPanel = ui.ChallengesPanel;
        var cpLine = $"ChallengesPanel null={challengesPanel == null}  vis={challengesPanel?.IsVisible}";
        ImGui.Text(cpLine); sb.AppendLine(cpLine);

        var bestiary = challengesPanel?.TabContainer?.BestiaryTab;
        var bLine = $"BestiaryTab null={bestiary == null}  vis={bestiary?.IsVisible}  addr={bestiary?.Address}";
        ImGui.Text(bLine); sb.AppendLine(bLine);

        CapturedBeastsTab cbp = null;
        try { cbp = bestiary?.CapturedBeastsTab; }
        catch (Exception ex) { ImGui.Text($"CapturedBeastsTab threw: {ex.Message}"); sb.AppendLine($"CapturedBeastsTab threw: {ex.Message}"); }
        var cbpLine = $"CapturedBeastsTab null={cbp == null}  vis={cbp?.IsVisible}  addr={cbp?.Address}";
        ImGui.Text(cbpLine); sb.AppendLine(cbpLine);

        // Use cached data — avoids re-reading 660 beast names per debug frame
        var countLine = $"CachedBeasts={_cachedBeasts.Count}  BeastPrices={Settings.BeastPrices.Count}";
        ImGui.Text(countLine); sb.AppendLine(countLine);

        ImGui.Separator();
        ImGui.Text("First 10 cached beasts:");
        sb.AppendLine("--- Cached Beasts ---");
        foreach (var entry in _cachedBeasts.Take(10))
        {
            var col = !entry.IsGenericYellow
                ? new System.Numerics.Vector4(0, 1, 0.4f, 1)
                : new System.Numerics.Vector4(1, 1, 0, 1);
            ImGui.TextColored(col, $"  \"{entry.DisplayName}\"  {entry.Price}c  yellow={entry.IsGenericYellow}");
            sb.AppendLine($"  \"{entry.DisplayName}\"  {entry.Price}c  yellow={entry.IsGenericYellow}");
        }

        ImGui.Separator();
        ImGui.Text("BeastPrices sample (first 10):");
        sb.AppendLine("--- BeastPrices sample ---");
        foreach (var kv in Settings.BeastPrices.Take(10))
        {
            sb.AppendLine($"  \"{kv.Key}\" = {kv.Value}c");
            ImGui.Text($"  \"{kv.Key}\" = {kv.Value}c");
        }

        if ((DateTime.Now - _lastDebugWrite).TotalSeconds >= 3)
        {
            try { System.IO.File.WriteAllText(DebugOutputPath, sb.ToString()); } catch { }
            _lastDebugWrite = DateTime.Now;
        }

        ImGui.Text($"[file: {DebugOutputPath}]");
        ImGui.End();
    }

    // ── Tracked beasts window ─────────────────────────────────────────────────

    private void DrawBeastsWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(0, 0));
        ImGui.SetNextWindowBgAlpha(0.6f);
        ImGui.Begin("Beasts Window", ImGuiWindowFlags.NoDecoration);

        if (_automationStatus != AutomationStatus.Idle)
        {
            if (_automationStatus == AutomationStatus.InProgress)
            {
                ImGui.TextColored(new System.Numerics.Vector4(0f, 1f, 0.4f, 1f), "● Automation in progress...");
            }
            else
            {
                var elapsed = (float)(DateTime.Now - _automationCompletedAt).TotalSeconds;
                var alpha = Math.Max(0f, 1f - elapsed / AutomationCompletedShowSeconds);
                ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.9f, 1f, alpha), "✓ Automation completed");
            }
            ImGui.Separator();
        }

        if (ImGui.BeginTable("Beasts Table", 2,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersV))
        {
            ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, 48);
            ImGui.TableSetupColumn("Beast");

            foreach (var trackedBeast in _trackedBeasts)
            {
                var entity = trackedBeast.Value;
                if (!BeastByPath.TryGetValue(entity.Metadata ?? "", out var beast)) continue;
                if (!_selectedBeastPathsSet.Contains(beast.Path)) continue;

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(Settings.BeastPrices.TryGetValue(beast.DisplayName, out var price)
                    ? $"{price.ToString(CultureInfo.InvariantCulture)}c"
                    : "0c");
                ImGui.TableNextColumn();
                ImGui.Text(beast.DisplayName);
                foreach (var craft in beast.Crafts)
                    ImGui.Text(craft);
            }

            for (int i = 0; i < _renderSnapshots.Count; i++)
            {
                var snap = _renderSnapshots[i];
                if (!snap.IsYellow) continue;

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.98f, 0f, 1f), "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.98f, 0f, 1f), snap.DisplayName);
            }

            ImGui.EndTable();
        }

        ImGui.End();
    }

    // ── Inventory / stash beast items ─────────────────────────────────────────

    private void DrawInventoryBeasts()
    {
        var inventory = GameController.Game.IngameState.IngameUi.InventoryPanel[InventoryIndex.PlayerInventory];
        if (!inventory.IsVisible) return;
        DrawCapturedBeasts(inventory.VisibleInventoryItems);
    }

    private void DrawStashBeasts()
    {
        var stash = GameController.Game.IngameState.IngameUi.StashElement;
        if (stash == null || !stash.IsVisible) return;
        var visibleStash = stash.VisibleStash;
        if (visibleStash == null) return;
        var items = visibleStash.VisibleInventoryItems;
        if (items == null) return;
        DrawCapturedBeasts(items);
    }

    private void DrawCapturedBeasts(IList<NormalInventoryItem> items)
    {
        if (items == null || items.Count == 0) return;

        foreach (var item in items)
        {
            if (item?.Item == null) continue;
            if (item.Item.Metadata != "Metadata/Items/Currency/CurrencyItemisedCapturedMonster") continue;

            var itemRect = item.GetClientRect();
            var monster = item.Item.GetComponent<CapturedMonster>();
            var monsterName = monster?.MonsterVariety?.MonsterName;

            if (!string.IsNullOrEmpty(monsterName) && Settings.BeastPrices.TryGetValue(monsterName, out var price))
            {
                Graphics.DrawBox(itemRect, new Color(0, 0, 0, 0.1f));
                Graphics.DrawText($"{price.ToString(CultureInfo.InvariantCulture)}c",
                    itemRect.Center, Color.White, FontAlign.Center);
            }
            else
            {
                Graphics.DrawBox(itemRect, new Color(255, 255, 0, 0.1f));
                Graphics.DrawFrame(itemRect, new Color(255, 255, 0, 0.2f), 1);
            }
        }
    }

    // ── Render snapshot builder ───────────────────────────────────────────────
    // Called from Tick() so all entity component reads and world-position math
    // happen outside Render. Value-type snapshot means no stale-entity risk.

    private void RebuildRenderSnapshots()
    {
        _renderSnapshots.Clear();

        var terrainData = GameController.IngameState.Data;
        var fillAlpha = Color.ToByte((int)(0.2f * byte.MaxValue));

        foreach (var kvp in _trackedBeasts)
        {
            var entity = kvp.Value;
            if (entity == null || !entity.IsValid || !entity.IsAlive) continue;

            var positioned = entity.GetComponent<Positioned>();
            if (positioned == null) continue;

            if (!BeastByPath.TryGetValue(entity.Metadata ?? "", out var beast)) continue;

            var isSelected = _selectedBeastPathsSet.Contains(beast.Path);
            var worldPos = terrainData.ToWorldWithTerrainHeight(positioned.GridPosition);
            var outline = GetSpecialBeastColor(beast.DisplayName);
            var fill = outline with { A = fillAlpha };
            var gridPos = new Vector2(positioned.GridPosNum.X, positioned.GridPosNum.Y);

            _renderSnapshots.Add(new BeastRenderSnapshot(
                kvp.Key,
                gridPos,
                worldPos,
                beast.DisplayName,
                fill,
                outline,
                Color.White,
                IsYellow: false,
                IsSelected: isSelected));
        }

        foreach (var kvp in _trackedYellowBeasts)
        {
            var entity = kvp.Value;
            if (entity == null || !entity.IsValid || !entity.IsAlive) continue;

            var positioned = entity.GetComponent<Positioned>();
            if (positioned == null) continue;

            var renderName = entity.GetComponent<Render>()?.Name ?? "Yellow Beast";
            var worldPos = terrainData.ToWorldWithTerrainHeight(positioned.GridPosition);
            var yellow = new Color(255, 250, 0);
            var fill = yellow with { A = fillAlpha };
            var gridPos = new Vector2(positioned.GridPosNum.X, positioned.GridPosNum.Y);

            _renderSnapshots.Add(new BeastRenderSnapshot(
                kvp.Key,
                gridPos,
                worldPos,
                renderName,
                fill,
                yellow,
                yellow,
                IsYellow: true,
                IsSelected: true));
        }
    }
}
