using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using NightmareAutomate.Helpers;
using NightmareAutomate.Services;

namespace NightmareAutomate.Strategies;

/// <summary>
/// Target selection for the combat hover. NEAR-FIRST: anything within
/// NearTargetRadius is fought before anything beyond it — the bot never runs
/// off through packs to a distant rare. (ACD totems keep working: their aura
/// only protects monsters near the TOTEM, so when invulnerable monsters are
/// hitting us, the totem is inside the near band too.) Within the chosen band:
/// ACD totems and uniques are ALWAYS on top (priority tier). Everything else
/// blends rarity with pack size — each rarity step is worth PackSizePerTier
/// clustered monsters, so a big enough magic pack outranks a lone rare and a
/// big enough white pack outranks a lone magic (hovering the pack center gives
/// ReAgent's AOE the most value). TIME-sticky: the current target is held for
/// TargetStickSeconds unless it dies, leaves the pool, or a priority target
/// (totem/unique) appears — prevents flip-flopping between equal picks.
///
/// Also owns the engage-stall bench: targets that could not be reached or
/// sighted after several walk attempts sit out of the pool for a few seconds
/// so targeting moves on to something reachable.
/// </summary>
public sealed class TargetSelector
{
    /// <summary>Targeted before EVERYTHING, even uniques — these totems make the
    /// monsters around them invulnerable, so nothing dies until they do.</summary>
    private static readonly string[] PriorityMonsterPaths = ["TotemAlliesCannotDie"];

    private readonly GameStateService _gameState;
    private readonly NightmareAutomateSettings _settings;
    private readonly Action<string> _log;

    private uint _currentTargetId;
    private long _targetAcquiredAt = Clock.Never;
    private readonly Dictionary<uint, long> _skipUntil = [];
    private readonly Dictionary<uint, int> _engageStalls = [];

    // Cluster-count cache keyed by the monsters-list INSTANCE: the strategy's
    // GetNearbyMonsters returns the same cached list object for 100ms, and Pick
    // also runs inside walk stopWhen lambdas at ~4Hz — the O(n²) pack scan must
    // run once per list generation, not once per call.
    private IReadOnlyList<Entity>? _clusterFor;
    private readonly Dictionary<uint, int> _clusterCounts = [];

    public TargetSelector(GameStateService gameState, NightmareAutomateSettings settings, Action<string> log)
    {
        _gameState = gameState;
        _settings = settings;
        _log = log;
    }

    /// <summary>
    /// PRIORITY tier: ACD totems above everything, then uniques — these always
    /// outrank any pack. Everything else scores 0 here and competes via the
    /// blended pack score instead. Used both for picking and for "did something
    /// strictly better appear" checks — cluster/distance deliberately not
    /// included, so ordinary packs can't abort walks or break the target stick.
    /// </summary>
    public static int TargetScore(Entity m) =>
        m.Path != null && PriorityMonsterPaths.Any(m.Path.Contains) ? 100
        : m.Rarity == MonsterRarity.Unique ? 10
        : 0;

    public Entity? Pick(IReadOnlyList<Entity> monsters)
    {
        // Benched targets sit out until their skip expires. If that leaves
        // nothing, we idle quietly (ReAgent still fights whatever is near)
        // instead of re-walking into the same wall.
        if (_skipUntil.Count > 0)
        {
            foreach (var expired in _skipUntil.Where(kv => Clock.Reached(kv.Value)).Select(kv => kv.Key).ToList())
                _skipUntil.Remove(expired);
            if (_skipUntil.Count > 0)
                monsters = monsters.Where(m => !_skipUntil.ContainsKey(m.Id)).ToList();
        }

        if (monsters.Count == 0)
        {
            _currentTargetId = 0;
            return null;
        }

        var playerPos = _gameState.PlayerGridPos;
        float nearRadius = _settings.NearTargetRadius;

        var near = monsters
            .Where(m => Vector2.Distance(playerPos, m.GridPosNum) <= nearRadius)
            .ToList();
        var pool = near.Count > 0 ? near : monsters;

        EnsureClusterCounts(monsters);

        float Dist(Entity m) => Vector2.Distance(playerPos, m.GridPosNum);

        // Rarity blended with pack size: one rarity step is worth PackSizePerTier
        // clustered monsters (lone rare = 2 steps, so a magic with a pack of
        // PackSizePerTier+1 around it wins; a white pack needs 2x+1).
        var packWeight = Math.Max(1, _settings.PackSizePerTier);
        int PackScore(Entity m) => (int)m.Rarity * packWeight + _clusterCounts.GetValueOrDefault(m.Id);

        // Priority tier first (totems, then uniques — always on top), then the
        // blended pack score, distance as the final tie-break.
        var best = pool
            .OrderByDescending(TargetScore)
            .ThenByDescending(PackScore)
            .ThenBy(Dist)
            .First();

        // Time-based stickiness: hold the current target briefly so equal picks
        // can't ping-pong between frames. A current target outside the pool
        // (e.g. far away while near monsters exist) is deliberately not searched
        // for — it must lose the target. A strictly higher priority tier always
        // switches instantly.
        var current = pool.FirstOrDefault(m => m.Id == _currentTargetId);
        if (current != null && best.Id != current.Id &&
            TargetScore(best) <= TargetScore(current) &&
            Clock.SecondsSince(_targetAcquiredAt) < _settings.TargetStickSeconds)
        {
            return current;
        }

        if (best.Id != _currentTargetId)
        {
            _currentTargetId = best.Id;
            _targetAcquiredAt = Clock.NowMs;
        }

        return best;
    }

    /// <summary>
    /// Engage stall: a walk toward the target ended with no ground gained and no
    /// range+sight on a still-valid target (dead-end wall, unwalkable pocket, no
    /// route). Re-walking it every tick would freeze the bot on it — after a few
    /// stalls the target is benched briefly so the pool moves on.
    /// </summary>
    public void ReportEngageStall(uint targetId)
    {
        var stalls = _engageStalls.GetValueOrDefault(targetId) + 1;
        _engageStalls[targetId] = stalls;
        if (stalls >= 3)
        {
            _engageStalls.Remove(targetId);
            _skipUntil[targetId] = Clock.InSeconds(5);
            _log("[Simulacrum] Can't reach or sight the target -- benching it for a bit.");
        }
    }

    public void ClearEngageStalls(uint targetId) => _engageStalls.Remove(targetId);

    private void EnsureClusterCounts(IReadOnlyList<Entity> monsters)
    {
        if (ReferenceEquals(_clusterFor, monsters)) return;
        _clusterFor = monsters;
        _clusterCounts.Clear();

        float clusterRadius = _settings.ClusterRadius;

        // Snapshot positions once — GridPosNum is a memory read per access, and
        // the pair loop below touches every position n times.
        var positions = new Vector2[monsters.Count];
        for (var i = 0; i < monsters.Count; i++)
            positions[i] = monsters[i].GridPosNum;

        for (var i = 0; i < monsters.Count; i++)
        {
            var count = 0;
            for (var j = 0; j < monsters.Count; j++)
            {
                if (Vector2.Distance(positions[i], positions[j]) <= clusterRadius)
                    count++;
            }

            _clusterCounts[monsters[i].Id] = count;
        }
    }

    public void Reset()
    {
        _currentTargetId = 0;
        _targetAcquiredAt = Clock.Never;
        _skipUntil.Clear();
        _engageStalls.Clear();
        _clusterFor = null;
        _clusterCounts.Clear();
    }
}
