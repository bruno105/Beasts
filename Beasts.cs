using System;
using System.Collections.Generic;
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

    private const string TrappedBuffName = "capture_monster_trapped";

    private static readonly HashSet<string> KnownBeastPaths = new(
        BeastsDatabase.AllBeasts.Select(b => b.Path).Where(p => !string.IsNullOrEmpty(p)),
        StringComparer.Ordinal
    );

    public override void OnLoad()
    {
        Settings.FetchBeastPrices.OnPressed += () => TriggerPriceRefresh(true);
        TriggerPriceRefresh(true);

        GameController.PluginBridge.SaveMethod("Beasts.IsAllowedBeastNearby", (int range) => IsAllowedBeastNearby(range));
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

        var beastsToRemove = new List<long>();

        foreach (var trackedBeast in _trackedBeasts)
        {
            var entity = trackedBeast.Value;
            if (entity == null || !entity.IsValid) continue;

            if (IsTrapped(entity))
            {
                beastsToRemove.Add(trackedBeast.Key);
            }
        }

        foreach (var id in beastsToRemove)
        {
            _trackedBeasts.Remove(id);
        }

        // Track yellow beasts (IsCapturableMonster but not in database)
        var yellowToRemove = new List<long>();
        foreach (var trackedYellow in _trackedYellowBeasts)
        {
            var entity = trackedYellow.Value;
            if (entity == null || !entity.IsValid)
            {
                yellowToRemove.Add(trackedYellow.Key);
                continue;
            }

            if (IsTrapped(entity))
            {
                yellowToRemove.Add(trackedYellow.Key);
            }
        }

        foreach (var id in yellowToRemove)
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
