using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Beasts.Data;
using Beasts.ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
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

    public override void Render()
    {
        DrawInGameBeasts();
        if (Settings.ShowBeastPricesOnLargeMap.Value) DrawBeastsOnLargeMap();
        if (Settings.ShowBestiaryPanel.Value) DrawBestiaryPanel();
        if (Settings.ShowTrackedBeastsWindow.Value) DrawBeastsWindow();
        if (Settings.ShowCapturedBeastsInInventory.Value) DrawInventoryBeasts();
        if (Settings.ShowCapturedBeastsInStash.Value) DrawStashBeasts();
    }

    private void DrawBeastsOnLargeMap()
    {
        var ingameUi = GameController.IngameState.IngameUi;

        _rect = GameController.Window.GetWindowRectangle() with { Location = SharpDX.Vector2.Zero };
        if (ingameUi.OpenRightPanel.IsVisible)
        {
            _rect.Right = ingameUi.OpenRightPanel.GetClientRectCache.Left;
        }

        if (ingameUi.OpenLeftPanel.IsVisible)
        {
            _rect.Left = ingameUi.OpenLeftPanel.GetClientRectCache.Right;
        }

        ImGui.SetNextWindowSize(new Vector2(_rect.Width, _rect.Height));
        ImGui.SetNextWindowPos(new Vector2(_rect.Left, _rect.Top));

        ImGui.Begin("beasts_radar_background",
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoBackground);

        _backGroundWindowPtr = ImGui.GetWindowDrawList();

        var map = ingameUi.Map;
        var largeMap = map.LargeMap.AsObject<SubMap>();
        if (largeMap.IsVisible)
        {
            var mapCenter = largeMap.MapCenter;
            _mapScale = largeMap.MapScale;
            DrawBeastsOnMap(mapCenter);
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

        foreach (var trackedBeast in _trackedBeasts)
        {
            var entity = trackedBeast.Value;
            var positioned = entity.GetComponent<Positioned>();
            if (positioned == null) continue;

            var beast = BeastsDatabase.AllBeasts.FirstOrDefault(b => entity.Metadata == b.Path);
            if (beast == null) continue;
            if (Settings.Beasts.All(b => b.Path != beast.Path)) continue;

            var beastPosition = new Vector2(positioned.GridPosNum.X, positioned.GridPosNum.Y);
            var beastGridPos = positioned.GridPosNum;

            float beastHeight = 0;
            int beastX = (int)beastGridPos.X;
            int beastY = (int)beastGridPos.Y;
            if (heightData != null && beastY >= 0 && beastY < heightData.Length
                && beastX >= 0 && beastX < heightData[beastY].Length)
            {
                beastHeight = heightData[beastY][beastX];
            }

            var mapDelta = TranslateGridDeltaToMapDelta(beastPosition - playerPosition, playerHeight + beastHeight);
            var mapPos = mapCenter + mapDelta;

            if (Settings.BeastPrices.TryGetValue(beast.DisplayName, out var price) && price > 0)
            {
                var text = $"{price.ToString(CultureInfo.InvariantCulture)}c";
                var textSize = Graphics.MeasureText(text);
                var textOffset = textSize / 2f;

                var bgPadding = new Vector2(4, 2);
                var bgColor = new Color(0, 0, 0, 180);
                DrawBox(mapPos - textOffset - bgPadding, mapPos + textOffset + bgPadding, bgColor);

                var color = GetSpecialBeastColor(beast.DisplayName);
                DrawText(text, mapPos - textOffset, color);
            }
        }
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
            ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(color.R / 255f, color.G / 255f,
                color.B / 255f, color.A / 255f)));
    }

    private void DrawText(string text, Vector2 pos, Color color)
    {
        _backGroundWindowPtr.AddText(pos,
            ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(color.R / 255f, color.G / 255f,
                color.B / 255f, color.A / 255f)), text);
    }

    private void DrawInGameBeasts()
    {
        foreach (var trackedBeast in _trackedBeasts
                     .Select(beast => new { Positioned = beast.Value.GetComponent<Positioned>(), beast.Value.Metadata })
                     .Where(beast => beast.Positioned != null))
        {
            var beast = BeastsDatabase.AllBeasts.First(beast => trackedBeast.Metadata == beast.Path);

            if (Settings.Beasts.All(b => b.Path != beast.Path)) continue;
            var pos = GameController.IngameState.Data.ToWorldWithTerrainHeight(trackedBeast.Positioned.GridPosition);
            Graphics.DrawText(beast.DisplayName, GameController.IngameState.Camera.WorldToScreen(pos), Color.White,
                FontAlign.Center);

            DrawFilledCircleInWorldPosition(pos, 100, GetSpecialBeastColor(beast.DisplayName));
        }
    }

    private static Color GetSpecialBeastColor(string beastName)
    {
        if (beastName.Contains("Vivid"))
        {
            return new Color(255, 250, 0);
        }

        if (beastName.Contains("Wild"))
        {
            return new Color(255, 0, 235);
        }

        if (beastName.Contains("Primal"))
        {
            return new Color(0, 245, 255);
        }

        if (beastName.Contains("Black"))
        {
            return new Color(255, 255, 255);
        }

        return Color.Red;
    }

    private void DrawBestiaryPanel()
    {
        var bestiary = GameController.IngameState.IngameUi.GetBestiaryPanel();
        if (bestiary == null || bestiary.IsVisible == false) return;

        var capturedBeastsPanel = bestiary.CapturedBeastsPanel;
        if (capturedBeastsPanel == null || capturedBeastsPanel.IsVisible == false) return;

        var beasts = bestiary.CapturedBeastsPanel.CapturedBeasts;
        foreach (var beast in beasts)
        {
            try
            {
                var beastMetadata = Settings.Beasts.Find(b => b.DisplayName == beast.DisplayName);
                if (beastMetadata == null) continue;
                if (!Settings.BeastPrices.ContainsKey(beastMetadata.DisplayName)) continue;

                var center = new Vector2(beast.GetClientRect().Center.X, beast.GetClientRect().Center.Y);

                Graphics.DrawBox(beast.GetClientRect(), new Color(0, 0, 0, 0.5f));
                Graphics.DrawFrame(beast.GetClientRect(), Color.White, 2);
                Graphics.DrawText(beastMetadata.DisplayName, center, Color.White, FontAlign.Center);

                var text = Settings.BeastPrices[beastMetadata.DisplayName].ToString(CultureInfo.InvariantCulture) + "c";
                var textPos = center + new Vector2(0, 20);
                Graphics.DrawText(text, textPos, Color.White, FontAlign.Center);
            }
            catch (Exception e)
            {
                // ignored
            }
        }
    }

    private void DrawBeastsWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(0, 0));
        ImGui.SetNextWindowBgAlpha(0.6f);
        ImGui.Begin("Beasts Window", ImGuiWindowFlags.NoDecoration);

        if (ImGui.BeginTable("Beasts Table", 2,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersV))
        {
            ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, 48);
            ImGui.TableSetupColumn("Beast");

            foreach (var beastMetadata in _trackedBeasts
                         .Select(trackedBeast => trackedBeast.Value)
                         .Select(beast => Settings.Beasts.Find(b => b.Path == beast.Metadata))
                         .Where(beastMetadata => beastMetadata != null))
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();

                ImGui.Text(Settings.BeastPrices.TryGetValue(beastMetadata.DisplayName, out var price)
                    ? $"{price.ToString(CultureInfo.InvariantCulture)}c"
                    : "0c");

                ImGui.TableNextColumn();

                ImGui.Text(beastMetadata.DisplayName);
                foreach (var craft in beastMetadata.Crafts)
                {
                    ImGui.Text(craft);
                }
            }

            ImGui.EndTable();
        }

        ImGui.End();
    }

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
                Graphics.DrawText($"{price.ToString(CultureInfo.InvariantCulture)}c", itemRect.Center,
                    Color.White, FontAlign.Center);
            }
            else
            {
                Graphics.DrawBox(itemRect, new Color(255, 255, 0, 0.1f));
                Graphics.DrawFrame(itemRect, new Color(255, 255, 0, 0.2f), 1);
            }
        }
    }

    private void DrawFilledCircleInWorldPosition(Vector3 position, float radius, Color color)
    {
        var circlePoints = new List<Vector2>();
        const int segments = 15;
        const float segmentAngle = 2f * MathF.PI / segments;

        for (var i = 0; i < segments; i++)
        {
            var angle = i * segmentAngle;
            var currentOffset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            var nextOffset = new Vector2(MathF.Cos(angle + segmentAngle), MathF.Sin(angle + segmentAngle)) * radius;

            var currentWorldPos = position + new Vector3(currentOffset, 0);
            var nextWorldPos = position + new Vector3(nextOffset, 0);

            circlePoints.Add(GameController.Game.IngameState.Camera.WorldToScreen(currentWorldPos));
            circlePoints.Add(GameController.Game.IngameState.Camera.WorldToScreen(nextWorldPos));
        }

        Graphics.DrawConvexPolyFilled(circlePoints.ToArray(),
            color with { A = Color.ToByte((int)((double)0.2f * byte.MaxValue)) });
        Graphics.DrawPolyLine(circlePoints.ToArray(), color, 2);
    }
}
