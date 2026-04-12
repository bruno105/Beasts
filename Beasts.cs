using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Beasts.Api;
using Beasts.Data;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;

namespace Beasts;

public partial class Beasts : BaseSettingsPlugin<BeastsSettings>
{
    private readonly Dictionary<long, Entity> _trackedBeasts = new();
    private readonly Dictionary<long, Entity> _trackedYellowBeasts = new();
    private int _isFetchingPrices;
    private DateTime _lastPriceRefreshAttemptUtc = DateTime.MinValue;

    // Reusable per-tick buffers -- avoid per-frame List<long> allocations.
    private readonly List<long> _beastsToRemoveBuffer = new(8);
    private readonly List<long> _yellowToRemoveBuffer = new(8);

    // Throttled rebuild of _selectedBeastPathsSet (moved from Render to Tick).
    private readonly Stopwatch _selectedPathsTimer = Stopwatch.StartNew();

    // Cached player grace_period state (read in Tick, consumed in Render).
    private bool _playerInGracePeriod;

    private const string TrappedBuffName = "capture_monster_trapped";

    private static readonly HashSet<string> KnownBeastPaths = new(
        BeastsDatabase.AllBeasts.Select(b => b.Path).Where(p => !string.IsNullOrEmpty(p)),
        StringComparer.Ordinal
    );

    // O(1) path → Beast lookup used in Render methods instead of O(n) FirstOrDefault/All
    internal static readonly Dictionary<string, Beast> BeastByPath =
        BeastsDatabase.AllBeasts
            .Where(b => !string.IsNullOrEmpty(b.Path))
            .ToDictionary(b => b.Path, b => b, StringComparer.Ordinal);

    public override void OnLoad()
    {
        Settings.FetchBeastPrices.OnPressed += () => TriggerPriceRefresh(true);
        TriggerPriceRefresh(true);

        GameController.PluginBridge.SaveMethod("Beasts.IsAllowedBeastNearby", (int range) => IsAllowedBeastNearby(range));

        Input.RegisterKey(Settings.Automation.Hotkey);
        Settings.Automation.Hotkey.OnValueChanged += () => Input.RegisterKey(Settings.Automation.Hotkey);
    }

    private void TriggerPriceRefresh(bool forceRefresh = false)
    {
        _ = FetchPrices(forceRefresh);
    }

    private async Task FetchPrices(bool forceRefresh = false)
    {
        if (!TryBeginPriceRefresh(forceRefresh)) return;

        DebugWindow.LogMsg("Fetching Beast Prices from PoeNinja...");
        try
        {
            var prices = await PoeNinja.GetBeastsPrices();
            foreach (var beast in BeastsDatabase.AllBeasts)
            {
                Settings.BeastPrices[beast.DisplayName] = prices.TryGetValue(beast.DisplayName, out var price) ? price : -1;
            }

            Settings.LastUpdate = DateTime.Now;
        }
        catch (Exception exception)
        {
            DebugWindow.LogMsg($"Failed to fetch Beast Prices from PoeNinja: {exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isFetchingPrices, 0);
        }
    }

    private bool TryBeginPriceRefresh(bool forceRefresh)
    {
        var refreshPeriodMinutes = Math.Max(1, Settings.PriceRefreshMinutes.Value);
        var nowUtc = DateTime.UtcNow;

        if (!forceRefresh)
        {
            if (!Settings.AutoRefreshPrices.Value) return false;
            if (_lastPriceRefreshAttemptUtc.AddMinutes(refreshPeriodMinutes) > nowUtc) return false;
        }

        if (Interlocked.CompareExchange(ref _isFetchingPrices, 1, 0) != 0) return false;

        _lastPriceRefreshAttemptUtc = nowUtc;
        return true;
    }

    public override Job Tick()
    {
        TriggerPriceRefresh();

        // Keep inventory snapshot up-to-date for automation inventory check
        var serverData = GameController?.Game?.IngameState?.Data?.ServerData;
        if (serverData?.PlayerInventories?.Count > 0)
            _automationInventory = serverData.PlayerInventories[0].Inventory;

        // Grace period scan -- cached for Render() to gate in-world draws.
        // Reads Buffs component here (in Tick) so Render never touches it.
        _playerInGracePeriod = false;
        var playerBuffs = GameController.Player?.GetComponent<Buffs>()?.BuffsList;
        if (playerBuffs != null)
        {
            for (int i = 0; i < playerBuffs.Count; i++)
            {
                if (playerBuffs[i]?.Name == "grace_period")
                {
                    _playerInGracePeriod = true;
                    break;
                }
            }
        }

        _beastsToRemoveBuffer.Clear();

        foreach (var trackedBeast in _trackedBeasts)
        {
            var entity = trackedBeast.Value;
            if (entity == null || !entity.IsValid) continue;

            if (IsTrapped(entity))
            {
                _beastsToRemoveBuffer.Add(trackedBeast.Key);
            }
        }

        foreach (var id in _beastsToRemoveBuffer)
        {
            _trackedBeasts.Remove(id);
        }

        // Track yellow beasts (IsCapturableMonster but not in database)
        _yellowToRemoveBuffer.Clear();
        foreach (var trackedYellow in _trackedYellowBeasts)
        {
            var entity = trackedYellow.Value;
            if (entity == null || !entity.IsValid)
            {
                _yellowToRemoveBuffer.Add(trackedYellow.Key);
                continue;
            }

            if (IsTrapped(entity))
            {
                _yellowToRemoveBuffer.Add(trackedYellow.Key);
            }
        }

        foreach (var id in _yellowToRemoveBuffer)
        {
            _trackedYellowBeasts.Remove(id);
        }

        // Scan for new yellow beasts not yet tracked
        foreach (var entity in GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
        {
            if (_trackedYellowBeasts.ContainsKey(entity.Id)) continue;
            if (!entity.IsValid || !entity.IsAlive) continue;
            if (IsTrapped(entity)) continue;

            var stats = entity.GetComponent<Stats>();
            if (stats == null) continue;
            if (!stats.StatDictionary.TryGetValue(GameStat.IsCapturableMonster, out var capVal) || capVal <= 0)
                continue;

            var metadata = entity.Metadata ?? "";
            var isKnown = false;
            foreach (var knownPath in KnownBeastPaths)
            {
                if (metadata.StartsWith(knownPath, StringComparison.Ordinal))
                {
                    isKnown = true;
                    break;
                }
            }

            if (!isKnown)
            {
                _trackedYellowBeasts[entity.Id] = entity;
            }
        }

        // Throttled in-place rebuild of _selectedBeastPathsSet (was in Render, now in Tick).
        // Clear + Add instead of the LINQ Select().Where().ToHashSet() chain.
        if (_selectedPathsTimer.ElapsedMilliseconds >= SelectedPathsRefreshMs)
        {
            _selectedBeastPathsSet.Clear();
            foreach (var b in Settings.Beasts)
            {
                if (!string.IsNullOrEmpty(b.Path))
                    _selectedBeastPathsSet.Add(b.Path);
            }
            _selectedPathsTimer.Restart();
        }

        // Build the per-frame render snapshot so DrawInGameBeasts/DrawBeastsOnMap
        // never need to touch entity components in Render.
        RebuildRenderSnapshots();

        return null;
    }

    private bool IsAllowedBeastNearby(int range)
    {
        return GetAllowedBeastsInRange(range).Any();
    }

    private static bool IsTrapped(Entity entity)
    {
        var buffs = entity.GetComponent<Buffs>();
        return buffs != null && buffs.BuffsList.Any(buff => buff.Name == TrappedBuffName);
    }

    private IEnumerable<Entity> GetAllowedBeastsInRange(int range)
    {
        var maxRange = Math.Max(1, range);
        var selectedPaths = Settings.Beasts
            .Select(beast => beast.Path)
            .Where(path => !string.IsNullOrEmpty(path))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entity in GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
        {
            if (!IsValidTargetMonster(entity, maxRange)) continue;

            var stats = entity.GetComponent<Stats>();
            if (stats == null) continue;

            if (!stats.StatDictionary.TryGetValue(GameStat.IsCapturableMonster, out var capVal) || capVal <= 0)
                continue;

            var metadata = entity.Metadata ?? "";
            var isRedBeast = false;
            foreach (var knownPath in KnownBeastPaths)
            {
                if (metadata.StartsWith(knownPath, StringComparison.Ordinal))
                {
                    if (selectedPaths.Contains(knownPath))
                        yield return entity;
                    isRedBeast = true;
                    break;
                }
            }

            if (!isRedBeast)
                yield return entity;
        }
    }

    private static bool IsValidTargetMonster(Entity entity, int maxRange)
    {
        if (entity == null || !entity.IsValid || !entity.IsAlive) return false;
        if (entity.DistancePlayer <= 0 || entity.DistancePlayer > maxRange) return false;
        if (!entity.TryGetComponent<Targetable>(out var targetable) || !targetable.isTargetable) return false;
        if (entity.GetComponent<Monster>() == null || entity.GetComponent<Positioned>() == null ||
            entity.GetComponent<Render>() == null || entity.GetComponent<Life>() == null ||
            entity.GetComponent<ObjectMagicProperties>() == null) return false;

        if (!entity.TryGetComponent<Buffs>(out var buffs)) return false;
        if (buffs.HasBuff("hidden_monster")) return false;

        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        _trackedBeasts.Clear();
        _trackedYellowBeasts.Clear();
        _renderSnapshots.Clear();
        _selectedBeastPathsSet.Clear();
        _beastsToRemoveBuffer.Clear();
        _yellowToRemoveBuffer.Clear();
        _playerInGracePeriod = false;
    }

    public override void EntityAdded(Entity entity)
    {
        if (entity.Rarity != MonsterRarity.Rare) return;
        foreach (var _ in BeastsDatabase.AllBeasts.Where(beast => entity.Metadata == beast.Path))
        {
            _trackedBeasts.Add(entity.Id, entity);
        }
    }

    public override void EntityRemoved(Entity entity)
    {
        _trackedBeasts.Remove(entity.Id);
        _trackedYellowBeasts.Remove(entity.Id);
    }
}
