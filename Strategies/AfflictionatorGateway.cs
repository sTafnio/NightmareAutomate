using System;
using System.Linq;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using NightmareAutomate.Helpers;
using NightmareAutomate.Models;
using NightmareAutomate.Services;

namespace NightmareAutomate.Strategies;

/// <summary>
/// Everything about the Simulacrum wave initiator ("Afflictionator") in one
/// place: finding it in the entity list, reading its StateMachine wave state,
/// and keeping the run counter in sync with it. Split out of the strategy so
/// the phase machine stays a phase machine.
/// </summary>
public sealed class AfflictionatorGateway
{
    /// <summary>Wave initiator entity. Both the containing folder AND the act suffix
    /// differ per layout (e.g. .../TownArena/Objects/AfflictionatorAct6 in Lunacy's
    /// Watch, .../Act8_DeliriumTown/Objects/AfflictionatorAct8 in The Syndrome
    /// Encampment) — only "/Objects/Afflictionator" is common to all. Must not match
    /// the AfflictionatorPackSpawnerDaemon monsters.</summary>
    public const string PathFragment = "/Objects/Afflictionator";

    private readonly GameStateService _gameState;
    private readonly AreaMemory _areas;
    private readonly Action<string> _log;

    private Entity? _cached;
    private long _scannedAt = Clock.Never;

    public AfflictionatorGateway(GameStateService gameState, AreaMemory areas, Action<string> log)
    {
        _gameState = gameState;
        _areas = areas;
        _log = log;
    }

    /// <summary>
    /// The wave initiator, when streamed into memory. Entities load/unload with
    /// distance — a null here often just means "too far away", which is why
    /// navigation falls back to the position remembered for this map instance.
    /// Remembers its position (and the in-area stash's) in AreaMemory when seen.
    /// Cached for 150ms: several phases call this every tick, and the lookup is
    /// a full entity-list scan with string matching.
    /// </summary>
    public Entity? Find()
    {
        if (Clock.MsSince(_scannedAt) < 150)
            return _cached is { IsValid: true } ? _cached : null;

        _scannedAt = Clock.NowMs;
        _cached = _gameState.FindEntitiesByPath(PathFragment)
            .FirstOrDefault(e => e.IsValid);

        var areaHash = _gameState.AreaHash;
        if (_cached != null)
            _areas.SetPoint(areaHash, AreaMemory.SimStartKey, _cached.GridPosNum);

        var stash = _gameState.FindStash();
        if (stash != null)
            _areas.SetPoint(areaHash, AreaMemory.SimStashKey, stash.GridPosNum);

        return _cached;
    }

    /// <summary>
    /// Read the wave state from the Afflictionator's StateMachine. Verified live:
    /// 'active' is the wave-running flag (1 during, 0 idle) BUT stays 1 forever once
    /// the run is complete; 'wave' is the wave counter and PERSISTS between waves
    /// (idle after wave 1 reads active:0/wave:1), so only 'active' may decide whether
    /// a wave is running; 'goodbye' flips to 1 exactly at 15/15 completion — the
    /// authoritative end-of-run signal.
    /// Nulls when the state can't be read (entity unloaded / component missing).
    /// </summary>
    public (bool? Active, int? WaveNumber, bool? Goodbye) GetWaveState(Entity? afflictionator) =>
        SafeRead.Try<(bool?, int?, bool?)>("aff.wave-state", () =>
        {
            var states = afflictionator?.GetComponent<StateMachine>()?.States;
            if (states == null) return (null, null, null);

            long active = 0, wave = 0, goodbye = 0;
            var found = false;
            foreach (var s in states)
            {
                if (s.Name == "active") { active = s.Value; found = true; }
                else if (s.Name == "wave") { wave = s.Value; }
                else if (s.Name == "goodbye") { goodbye = s.Value; }
            }

            if (!found) return (null, null, null);
            return (active != 0, wave > 0 ? (int)wave : null, goodbye != 0);
        });

    public bool? IsWaveActive(Entity? afflictionator) => GetWaveState(afflictionator).Active;

    public bool? IsRunComplete(Entity? afflictionator) => GetWaveState(afflictionator).Goodbye;

    /// <summary>
    /// The initiator's wave counter is the truth — e.g. when the bot is started at
    /// wave 5, our own count would otherwise begin at 1. Except right after a wave
    /// start: 'wave' lags ~1 s behind 'active' (verified in the logs: "wave 13
    /// started ... wave=13", then "synced 13 -> 14" 0.8 s later), so callers hold
    /// off syncing for a moment after counting a start themselves.
    /// False when the counter is not readable (0 also reads as unknown).
    /// </summary>
    public bool SyncWaveNumber(RunContext run, Entity? afflictionator)
    {
        var (_, waveNumber, _) = GetWaveState(afflictionator);
        if (!waveNumber.HasValue) return false;

        if (waveNumber.Value != run.WavesStarted)
        {
            _log($"[Simulacrum] Wave counter synced from initiator: {run.WavesStarted} -> {waveNumber.Value}.");
            run.WavesStarted = waveNumber.Value;
        }
        return true;
    }

    /// <summary>Log a wave start/end, plus (as a routine step) the raw StateMachine
    /// values — the record of what the initiator actually reported.</summary>
    public void LogStates(Entity? afflictionator, string context)
    {
        try
        {
            var states = afflictionator?.GetComponent<StateMachine>()?.States;
            var dump = states == null
                ? "n/a"
                : string.Join(", ", states.Select(s => $"{s.Name}={s.Value}"));
            _log($"[Simulacrum] {context}.");
            BotLog.Step($"[Simulacrum] Initiator states: {dump}");
        }
        catch (Exception ex)
        {
            SafeRead.Note("aff.log-states", ex);
            _log($"[Simulacrum] {context}.");
        }
    }

    public void Reset()
    {
        _cached = null;
        _scannedAt = Clock.Never;
    }
}
