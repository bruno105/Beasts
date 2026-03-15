using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Beasts.Api;
using Beasts.Data;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using static ExileCore.Shared.Nodes.HotkeyNodeV2;

namespace Beasts;

public partial class Beasts : BaseSettingsPlugin<BeastsSettings>
{
    private readonly Dictionary<long, Entity> _trackedBeasts = new();

    private static readonly HashSet<string> KnownBeastPaths = new(
        BeastsDatabase.AllBeasts.Select(b => b.Path).Where(p => !string.IsNullOrEmpty(p)),
        StringComparer.Ordinal
    );

    private bool _hasLoggedMagicInputMissing;
    private bool _hasLoggedHotkeyFallbackMissing;

    public override void OnLoad()
    {
        Settings.FetchBeastPrices.OnPressed += async () => await FetchPrices();
        Task.Run(FetchPrices);

        GameController.PluginBridge.SaveMethod("Beasts.IsAllowedBeastNearby", (int range) => IsAllowedBeastNearby(range));
        GameController.PluginBridge.SaveMethod("Beasts.CastSkillOnAllowedBeast", (uint skillId, int range) =>
            CastSkillOnAllowedBeast(skillId, range));
        GameController.PluginBridge.SaveMethod("Beasts.CastNamedSkillOnAllowedBeast", (string skillName, int range) =>
            CastNamedSkillOnAllowedBeast(skillName, range));
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
        var allowed = GetAllowedBeastsInRange(range).ToList();
        if (allowed.Count > 0)
        {
            DebugWindow.LogMsg($"[Beasts Bridge] Allowed beast nearby: {allowed[0].Metadata} (total: {allowed.Count})", 2);
        }
        return allowed.Count > 0;
    }

    private bool CastSkillOnAllowedBeast(uint skillId, int range)
    {
        var target = GetAllowedBeastsInRange(range)
            .OrderBy(entity => entity.DistancePlayer)
            .FirstOrDefault();
        if (target == null) return false;

        var castWithTarget = GameController.PluginBridge.GetMethod<Action<Entity, uint>>("MagicInput.CastSkillWithTarget");
        if (castWithTarget != null)
        {
            castWithTarget(target, skillId);
            _hasLoggedMagicInputMissing = false;
            _hasLoggedHotkeyFallbackMissing = false;
            return true;
        }

        if (!_hasLoggedMagicInputMissing)
        {
            _hasLoggedMagicInputMissing = true;
            DebugWindow.LogMsg("[Beasts] MagicInput bridge unavailable. Using hotkey fallback.", 10);
        }

        var fallbackResult = TryCastUsingSkillHotkey(skillId, target);
        if (!fallbackResult && !_hasLoggedHotkeyFallbackMissing)
        {
            _hasLoggedHotkeyFallbackMissing = true;
            DebugWindow.LogError("[Beasts] Failed to resolve skill hotkey fallback for allowed beast cast.", 10);
        }
        else if (fallbackResult)
        {
            _hasLoggedHotkeyFallbackMissing = false;
        }

        return fallbackResult;
    }

    private bool CastNamedSkillOnAllowedBeast(string skillName, int range)
    {
        if (string.IsNullOrWhiteSpace(skillName)) return false;

        var actor = GameController.Player?.GetComponent<Actor>();
        var actorSkill = actor?.ActorSkills?.FirstOrDefault(skill =>
            string.Equals(skill.Name, skillName, StringComparison.OrdinalIgnoreCase));
        if (actorSkill == null || !actorSkill.CanBeUsed) return false;

        return CastSkillOnAllowedBeast(actorSkill.Id, range);
    }

    private bool TryCastUsingSkillHotkey(uint skillId, Entity target)
    {
        var actor = GameController.Player?.GetComponent<Actor>();
        var actorSkill = actor?.ActorSkills?.FirstOrDefault(skill => skill.Id == skillId);
        if (actorSkill == null || !actorSkill.CanBeUsed) return false;

        var hotkey = ResolveSkillHotkey(actorSkill);
        if (hotkey == null) return false;

        var targetScreenPos = GameController.IngameState.Camera.WorldToScreen(target.PosNum);
        Input.SetCursorPos(targetScreenPos);
        InputHelper.SendInputPress(new HotkeyNodeValue(hotkey.Value));
        return true;
    }

    private Keys? ResolveSkillHotkey(ActorSkill actorSkill)
    {
        var shortcuts = GameController.IngameState?.ShortcutSettings?.Shortcuts?.ToList();
        if (shortcuts == null || shortcuts.Count == 0) return null;

        var directIndex = actorSkill.SkillSlotIndex;
        if (directIndex >= 0 && directIndex < shortcuts.Count)
        {
            var key = (Keys)shortcuts[directIndex].MainKey;
            if (key != Keys.None) return key;
        }

        if (directIndex > 0 && directIndex - 1 < shortcuts.Count)
        {
            var key = (Keys)shortcuts[directIndex - 1].MainKey;
            if (key != Keys.None) return key;
        }

        return null;
    }

    private static DateTime _lastDebugLog = DateTime.MinValue;

    private IEnumerable<Entity> GetAllowedBeastsInRange(int range)
    {
        var maxRange = Math.Max(1, range);
        var selectedPaths = Settings.Beasts
            .Select(beast => beast.Path)
            .Where(path => !string.IsNullOrEmpty(path))
            .ToHashSet(StringComparer.Ordinal);

        var shouldLog = (DateTime.Now - _lastDebugLog).TotalSeconds >= 3;

        foreach (var entity in GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
        {
            if (!IsValidTargetMonster(entity, maxRange)) continue;

            var stats = entity.GetComponent<Stats>();
            if (stats == null) continue;

            var isCapturable = stats.StatDictionary.TryGetValue(GameStat.IsCapturableMonster, out var capVal) && capVal > 0;
            if (!isCapturable) continue;

            // Check if this is a known red beast (exact match or variant/minion via StartsWith)
            var metadata = entity.Metadata ?? "";
            var matchedRedPath = (string)null;
            foreach (var knownPath in KnownBeastPaths)
            {
                if (metadata.StartsWith(knownPath, StringComparison.Ordinal))
                {
                    matchedRedPath = knownPath;
                    break;
                }
            }

            if (matchedRedPath != null)
            {
                // Red beast (or its minion/variant) — only allow if selected
                if (selectedPaths.Contains(matchedRedPath))
                {
                    if (shouldLog)
                    {
                        DebugWindow.LogMsg($"[Beasts Debug] RED SELECTED allowed: {metadata}", 5);
                        _lastDebugLog = DateTime.Now;
                    }
                    yield return entity;
                }
                else if (shouldLog)
                {
                    DebugWindow.LogMsg($"[Beasts Debug] RED UNSELECTED blocked: {metadata}", 5);
                    _lastDebugLog = DateTime.Now;
                }
            }
            else
            {
                // Yellow beast — always allow
                if (shouldLog)
                {
                    DebugWindow.LogMsg($"[Beasts Debug] YELLOW allowed: {metadata}", 5);
                    _lastDebugLog = DateTime.Now;
                }
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