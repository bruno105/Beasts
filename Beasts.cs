using System;
using System.Collections.Generic;
using System.Linq;
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
    private static readonly HashSet<string> RedBestiaryPaths = new(StringComparer.Ordinal)
    {
        "Metadata/Monsters/LeagueBestiary/GemFrogBestiary",
        "Metadata/Monsters/LeagueBestiary/WolfBestiary",
        "Metadata/Monsters/LeagueBestiary/LynxBestiary",
        "Metadata/Monsters/LeagueBestiary/HellionBestiary2",
        "Metadata/Monsters/LeagueBestiary/TigerBestiary",
        "Metadata/Monsters/LeagueBestiary/SpiderPlatedBestiary",
        "Metadata/Monsters/LeagueBestiary/Avians/MarakethBirdBestiary",
        "Metadata/Monsters/LeagueBestiary/CrabSpiderBestiary",
        "Metadata/Monsters/LeagueBestiary/TigerBestiarySpiritBoss",
        "Metadata/Monsters/LeagueBestiary/SpiderPlatedBestiarySpiritBoss",
        "Metadata/Monsters/LeagueBestiary/MarakethBirdSpiritBoss",
        "Metadata/Monsters/LeagueBestiary/NessaCrabBestiarySpiritBoss"
    };

    private bool _hasLoggedMagicInputMissing;

    public override void OnLoad()
    {
        Settings.FetchBeastPrices.OnPressed += async () => await FetchPrices();
        Task.Run(FetchPrices);

        GameController.PluginBridge.SaveMethod("Beasts.IsAllowedBeastNearby", (int range) => IsAllowedBeastNearby(range));
        GameController.PluginBridge.SaveMethod("Beasts.CastSkillOnAllowedBeast", (uint skillId, int range) =>
            CastSkillOnAllowedBeast(skillId, range));
    }

    private async Task FetchPrices()
    {
        DebugWindow.LogMsg("Fetching Beast Prices from PoeNinja...");
        var prices = await PoeNinja.GetBeastsPrices();
        foreach (var beast in BeastsDatabase.AllBeasts)
        {
            Settings.BeastPrices[beast.DisplayName] = prices.TryGetValue(beast.DisplayName, out var price) ? price : -1;
        }

        Settings.LastUpdate = DateTime.Now;
    }

    public override Job Tick()
    {
        var beastsToRemove = new List<long>();

        foreach (var trackedBeast in _trackedBeasts)
        {
            var entity = trackedBeast.Value;
            if (entity == null || !entity.IsValid) continue;

            var buffs = entity.GetComponent<Buffs>();
            if (buffs != null && buffs.BuffsList.Any(buff => buff.Name == "capture_monster_trapped"))
            {
                beastsToRemove.Add(trackedBeast.Key);
            }
        }

        foreach (var id in beastsToRemove)
        {
            _trackedBeasts.Remove(id);
        }

        return null;
    }

    private bool IsAllowedBeastNearby(int range)
    {
        return GetAllowedBeastsInRange(range).Any();
    }

    private bool CastSkillOnAllowedBeast(uint skillId, int range)
    {
        var target = GetAllowedBeastsInRange(range)
            .OrderBy(entity => entity.DistancePlayer)
            .FirstOrDefault();
        if (target == null) return false;

        var castWithTarget = GameController.PluginBridge.GetMethod<Action<Entity, uint>>("MagicInput.CastSkillWithTarget");
        if (castWithTarget == null)
        {
            if (!_hasLoggedMagicInputMissing)
            {
                _hasLoggedMagicInputMissing = true;
                DebugWindow.LogError("[Beasts] MagicInput.CastSkillWithTarget bridge is unavailable.", 10);
            }

            return false;
        }

        castWithTarget(target, skillId);
        _hasLoggedMagicInputMissing = false;
        return true;
    }

    private IEnumerable<Entity> GetAllowedBeastsInRange(int range)
    {
        var maxRange = Math.Max(1, range);
        var selectedRedPaths = Settings.Beasts
            .Select(beast => beast.Path)
            .Where(path => !string.IsNullOrEmpty(path))
            .Where(path => RedBestiaryPaths.Contains(path))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entity in GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
        {
            if (!IsValidTargetMonster(entity, maxRange)) continue;

            var metadata = entity.Metadata;
            if (string.IsNullOrEmpty(metadata)) continue;
            if (!metadata.StartsWith("Metadata/Monsters/LeagueBestiary/", StringComparison.Ordinal)) continue;

            var isRedBeast = RedBestiaryPaths.Contains(metadata);
            if (!isRedBeast)
            {
                yield return entity;
                continue;
            }

            if (selectedRedPaths.Contains(metadata))
            {
                yield return entity;
            }
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
        if (_trackedBeasts.ContainsKey(entity.Id))
        {
            _trackedBeasts.Remove(entity.Id);
        }
    }
}