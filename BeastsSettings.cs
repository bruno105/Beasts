using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Beasts.Data;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Helpers;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;
using SharpDX;

namespace Beasts;

public class BeastsSettings : ISettings
{
    public List<Beast> Beasts { get; set; } = new();
    public Dictionary<string, float> BeastPrices { get; set; } = new();
    public DateTime LastUpdate { get; set; } = DateTime.MinValue;

    public BeastsSettings()
    {
        BeastPicker = new CustomNode
        {
            DrawDelegate = () =>
            {
                ImGui.Separator();
                if (ImGui.BeginTable("BeastsTable", 4,
                        ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Sortable |
                        ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersV |
                        ImGuiTableFlags.NoBordersInBody | ImGuiTableFlags.ScrollY))
                {
                    ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.WidthFixed, 24);
                    ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, 48);
                    ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, 256);
                    ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableHeadersRow();

                    var sortedBeasts = BeastsDatabase.AllBeasts;
                    if (ImGui.TableGetSortSpecs() is { SpecsDirty: true } sortSpecs)
                    {
                        int sortedColumn = sortSpecs.Specs.ColumnIndex;
                        var sortAscending = sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending;

                        sortedBeasts = sortedColumn switch
                        {
                            0 => sortAscending
                                ? [.. sortedBeasts.OrderBy(b => Beasts.Any(eb => eb.Path == b.Path))]
                                : [.. sortedBeasts.OrderByDescending(b => Beasts.Any(eb => eb.Path == b.Path))],
                            1 => sortAscending
                                ? [.. sortedBeasts.OrderBy(b => BeastPrices[b.DisplayName])]
                                : [.. sortedBeasts.OrderByDescending(b => BeastPrices[b.DisplayName])],
                            2 => sortAscending
                                ? [.. sortedBeasts.OrderBy(b => b.DisplayName)]
                                : [.. sortedBeasts.OrderByDescending(x => x.DisplayName)],
                            3 => sortAscending
                                ? [.. sortedBeasts.OrderBy(b => b.Crafts[0])]
                                : [.. sortedBeasts.OrderByDescending(x => x.Crafts[0])],
                            _ => sortAscending
                                ? [.. sortedBeasts.OrderBy(b => b.DisplayName)]
                                : [.. sortedBeasts.OrderByDescending(x => x.DisplayName)]
                        };
                    }

                    foreach (var beast in sortedBeasts)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();

                        var isChecked = Beasts.Any(eb => eb.Path == beast.Path);
                        if (ImGui.Checkbox($"##{beast.Path}", ref isChecked))
                        {
                            if (isChecked)
                            {
                                Beasts.Add(beast);
                            }
                            else
                            {
                                Beasts.RemoveAll(eb => eb.Path == beast.Path);
                            }
                        }

                        if (isChecked)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, Color.Green.ToImguiVec4());
                        }

                        ImGui.TableNextColumn();
                        ImGui.Text(BeastPrices.TryGetValue(beast.DisplayName, out var price) ? $"{price}c" : "0c");

                        ImGui.TableNextColumn();
                        ImGui.Text(beast.DisplayName);

                        ImGui.TableNextColumn();
                        // display all the crafts for the beast seperated by newline
                        foreach (var craft in beast.Crafts)
                        {
                            ImGui.Text(craft);
                        }

                        if (isChecked)
                        {
                            ImGui.PopStyleColor();
                        }

                        ImGui.NextColumn();
                    }

                    ImGui.EndTable();
                }
            }
        };

        LastUpdated = new CustomNode
        {
            DrawDelegate = () =>
            {
                ImGui.Text("PoeNinja prices as of:");
                ImGui.SameLine();
                ImGui.Text(LastUpdate.ToString("HH:mm:ss"));
            }
        };
    }

    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    public ToggleNode ShowTrackedBeastsWindow { get; set; } = new ToggleNode(true);

    public ToggleNode ShowBeastPricesOnLargeMap { get; set; } = new ToggleNode(true);
    
    public ToggleNode ShowCapturedBeastsInInventory { get; set; } = new ToggleNode(true);
    
    public ToggleNode ShowCapturedBeastsInStash { get; set; } = new ToggleNode(true);
    
    public ToggleNode ShowBestiaryPanel { get; set; } = new ToggleNode(true);

    public ToggleNode ShowAllPricesInBestiaryPanel { get; set; } = new ToggleNode(true);

    public ToggleNode ShowBestiaryDebug { get; set; } = new ToggleNode(false);

    public BeastAutomationSettings Automation { get; set; } = new();

    public ToggleNode AutoRefreshPrices { get; set; } = new ToggleNode(true);

    public RangeNode<int> PriceRefreshMinutes { get; set; } = new(15, 1, 60);

    public ButtonNode FetchBeastPrices { get; set; } = new ButtonNode();

    [JsonIgnore] public CustomNode LastUpdated { get; set; }

    [JsonIgnore] public CustomNode BeastPicker { get; set; }
}

[Submenu(CollapsedByDefault = false)]
public class BeastAutomationSettings
{
    /// <summary>Toggle to start/stop the automation loop.</summary>
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    /// <summary>Hotkey to toggle automation on/off without opening settings.</summary>
    public HotkeyNode Hotkey { get; set; } = new HotkeyNode(Keys.None);

    /// <summary>
    /// Beasts worth this value or more are itemized; all others are released.
    /// Yellow beasts not tracked by poe.ninja are treated as 0c and always released
    /// unless ItemizeYellowBeasts is ON.
    /// </summary>
    public RangeNode<int> ItemizeAboveChaos { get; set; } = new RangeNode<int>(4, 0, 500);

    /// <summary>When ON, itemize all yellow beasts regardless of their price.</summary>
    public ToggleNode ItemizeYellowBeasts { get; set; } = new ToggleNode(false);

    /// <summary>When ON, stop the automation if inventory is full. Recommended: ON.</summary>
    public ToggleNode CheckInventoryBeforeItemize { get; set; } = new ToggleNode(true);

    /// <summary>
    /// Input mode: SimpleDelay uses a bare-minimum configurable delay,
    /// InputHumanizer delegates all mouse input to the InputHumanizer plugin via PluginBridge.
    /// </summary>
    public ListNode InputMode { get; set; } = new ListNode { Value = "SimpleDelay" };

    /// <summary>
    /// Fixed delay (ms) between consecutive itemize/release actions.
    /// Increase if the automation misses clicks due to UI lag.
    /// </summary>
    public RangeNode<int> ActionDelayMs { get; set; } = new RangeNode<int>(300, 50, 3000);

    /// <summary>Input timing settings (SimpleDelay mode only).</summary>
    public BeastDelayOptions Delays { get; set; } = new();
}

[Submenu(CollapsedByDefault = true)]
public class BeastDelayOptions
{
    /// <summary>
    /// Delay (ms) after moving the cursor to a button before clicking.
    /// Only used in SimpleDelay mode.
    /// </summary>
    public RangeNode<int> PreClickDelayMs { get; set; } = new RangeNode<int>(30, 5, 300);
}