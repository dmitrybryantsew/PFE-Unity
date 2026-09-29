using System;
using System.Text;
using MessagePipe;
using PFE.Core.Messages;
using PFE.Data;
using PFE.Data.Definitions;
using PFE.Entities.Player;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Map.Streaming;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// Debug commands exposed to the Lua console as the global <c>player</c> table, so they read
    /// like the AS3-era console: <c>player:Heal(50)</c>, <c>player:GiveWeapon("pistol")</c>.
    ///
    /// <para><b>Why these live outside <c>DeveloperConsoleService</c>.</b> The service is
    /// deliberately UnityEngine-free so it can be unit-tested headlessly (its own doc comment says
    /// so). These commands need a live <c>PlayerController</c>, a <c>LandMap</c> and scene lookups,
    /// so they are kept in a separate class that the controller wires up — the service keeps its
    /// testability and the commands get their scene access.</para>
    ///
    /// <para><b>MoonSharp visibility.</b> The wiring members are <c>private</c>; MoonSharp's default
    /// reflection interop exposes public members only, so only the command methods below become
    /// callable from Lua. Do not make the wiring fields public.</para>
    /// </summary>
    [LocalOnly]
    public sealed class DevConsolePlayerCommands
    {
        private LandMap _landMap;
        private GameDatabase _database;
        private PlayerWeaponLoadout _loadout;
        private IPublisher<HealMessage> _healPublisher;

        /// <summary>
        /// Late-resolved so the commands survive the player being respawned or replaced. Resolved
        /// per call rather than cached: a cached reference to a destroyed player is the classic
        /// "MissingReferenceException in a debug tool" failure.
        /// </summary>
        private Func<PlayerController> _playerProvider;

        internal void Wire(
            LandMap landMap,
            GameDatabase database,
            PlayerWeaponLoadout loadout,
            IPublisher<HealMessage> healPublisher,
            Func<PlayerController> playerProvider)
        {
            _landMap = landMap;
            _database = database;
            _loadout = loadout;
            _healPublisher = healPublisher;
            _playerProvider = playerProvider;
        }

        private static PlayerController FindPlayer()
            => UnityEngine.Object.FindFirstObjectByType<PlayerController>();

        private PlayerController Player()
        {
            var player = _playerProvider != null ? _playerProvider() : null;
            return player != null ? player : FindPlayer();
        }

        // ── Health ────────────────────────────────────────────────────────────

        /// <summary>Heal the player by <paramref name="amount"/> and float the number on screen.</summary>
        public string Heal(float amount)
        {
            var player = Player();
            if (player?.Stats == null) return "[player] No player in scene.";

            player.Stats.Heal(amount);

            // Publishing is what makes this visible: FloatingTextManager renders the "+N", and it
            // only ever sees heals through this broker (registered in GameLifetimeScope).
            _healPublisher?.Publish(new HealMessage
            {
                amount = amount,
                position = player.transform.position
            });

            return $"[player] Healed {amount} -> {player.Stats.CurrentHp.Value:0.#}/{player.Stats.MaxHp.Value:0.#}";
        }

        /// <summary>Damage the player by <paramref name="amount"/>.</summary>
        public string Damage(float amount)
        {
            var player = Player();
            if (player?.Stats == null) return "[player] No player in scene.";

            player.Stats.Damage(amount);
            return $"[player] Damaged {amount} -> {player.Stats.CurrentHp.Value:0.#}/{player.Stats.MaxHp.Value:0.#}";
        }

        /// <summary>Set the player's current HP to an absolute value.</summary>
        public string SetHealth(float value)
        {
            var player = Player();
            if (player?.Stats == null) return "[player] No player in scene.";

            float delta = value - player.Stats.CurrentHp.Value;
            if (delta >= 0f) player.Stats.Heal(delta);
            else player.Stats.Damage(-delta);

            return $"[player] HP set to {player.Stats.CurrentHp.Value:0.#}/{player.Stats.MaxHp.Value:0.#}";
        }

        /// <summary>Kill the player outright.</summary>
        public string Kill()
        {
            var player = Player();
            if (player?.Stats == null) return "[player] No player in scene.";

            player.Stats.Damage(player.Stats.MaxHp.Value + 1f);
            return "[player] Player killed.";
        }

        // ── Weapons ───────────────────────────────────────────────────────────

        /// <summary>Equip a weapon by its content id, e.g. <c>player:GiveWeapon("10mm")</c>.</summary>
        public string GiveWeapon(string weaponId)
        {
            if (string.IsNullOrWhiteSpace(weaponId))
                return "[player] GiveWeapon requires a weapon id.";

            if (_loadout == null)
                return "[player] No PlayerWeaponLoadout in scene.";

            if (_database == null)
                return "[player] GameDatabase not available.";

            WeaponDefinition def = _database.GetWeapon(weaponId);
            if (def == null)
                return $"[player] No weapon with id '{weaponId}'. Check the id in the weapon assets.";

            _loadout.Equip(def);
            return $"[player] Equipped '{def.weaponId}'.";
        }

        /// <summary>Refill the equipped weapon's magazine to full.</summary>
        public string RefillAmmo()
        {
            var state = _loadout?.Current?.State;
            if (state == null) return "[player] No weapon equipped.";

            state.CurrentAmmo = state.Def.magazineSize;
            state.SyncReactive();
            return $"[player] Ammo refilled to {state.CurrentAmmo}/{state.Def.magazineSize}.";
        }

        // ── Movement ──────────────────────────────────────────────────────────

        /// <summary>Teleport to an absolute world position.</summary>
        public string Teleport(float x, float y)
        {
            var player = Player();
            if (player == null) return "[player] No player in scene.";

            player.transform.position = new Vector3(x, y, player.transform.position.z);
            return $"[player] Teleported to ({x:0.#}, {y:0.#}).";
        }

        /// <summary>
        /// Teleport to the room at land coordinate (<paramref name="x"/>, <paramref name="y"/>).
        /// Uses the same activation path as a door transition so the room's visuals, streaming and
        /// fog follow — moving the transform alone would leave the player in a room that is not the
        /// active one, which is exactly the desync LandMap.SetCurrentRoom's comment describes.
        /// </summary>
        public string TeleportToRoom(int x, int y)
        {
            var player = Player();
            if (player == null) return "[player] No player in scene.";

            if (_landMap == null) return "[player] LandMap not available.";

            var coord = new Vector3Int(x, y, 0);
            RoomInstance room = _landMap.GetRoom(coord);
            if (room == null)
                return $"[player] No room at ({x}, {y}).";

            Vector3 spawn = RoomSetup.FindPlayerSpawnUnity(room);

            // Explicit cast: SwitchRoom takes Vector2? and chaining a user-defined Vector3->Vector2
            // conversion into the nullable wrap is exactly the kind of implicit that reads as fine
            // and compiles differently than it looks.
            _landMap.SwitchRoom(coord, (Vector2)spawn);

            // SwitchRoom only moves the LandMap's own bookkeeping. The rest of the swap - streaming
            // activation (which also rebuilds the low-level physics world geometry), the re-render
            // and the camera snap - is RoomTransitionManager's job, and this command used to skip
            // all of it while its doc comment claimed otherwise: the room was never activated in
            // the streaming manager, so the room the player teleported INTO never ticked, and the
            // visuals still came from the room they left.
            var transitionManager = UnityEngine.Object.FindFirstObjectByType<RoomTransitionManager>();
            if (transitionManager != null)
            {
                transitionManager.ApplyRestoredRoom(room);
            }
            else
            {
                // No transition manager in the scene (bare test scene): do the one step that was
                // always done here, so the room at least becomes the active one.
                var streaming = UnityEngine.Object.FindFirstObjectByType<RoomStreamingManager>();
                if (streaming != null)
                {
                    streaming.ActivateRoom(room, _landMap.previousRoom);
                }
            }

            // The transform move alone desyncs the movement motor. TilePhysicsController keeps its
            // own pixel position and WRITES transform.position from it every tick, so a raw move is
            // undone on the next tick and the player snaps back to where they were.
            var motor = player.GetComponent<PFE.Systems.Physics.IMovementMotor>();
            if (motor != null)
            {
                motor.RepositionForRoom(room, spawn);
            }
            else
            {
                player.transform.position = spawn;
            }

            return $"[player] Teleported to room ({x}, {y}) at {spawn}.";
        }

        // ── Enemies ───────────────────────────────────────────────────────────

        /// <summary>Kill every non-player unit in the scene.</summary>
        public string KillAll()
        {
            var units = UnityEngine.Object.FindObjectsByType<UnitController>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            int killed = 0;
            foreach (var unit in units)
            {
                if (unit == null || unit.IsPlayer || !unit.IsAlive) continue;

                unit.TakeDamage(unit.MaxHealth + 1f);
                killed++;
            }

            return $"[player] Killed {killed} unit(s).";
        }

        /// <summary>Report the player's position, health and equipped weapon.</summary>
        public string Status()
        {
            var player = Player();
            if (player?.Stats == null) return "[player] No player in scene.";

            var sb = new StringBuilder();
            sb.Append($"[player] pos={player.transform.position} ");
            sb.Append($"hp={player.Stats.CurrentHp.Value:0.#}/{player.Stats.MaxHp.Value:0.#} ");

            var state = _loadout?.Current?.State;
            sb.Append(state != null
                ? $"weapon='{state.Def.weaponId}' ammo={state.CurrentAmmo}/{state.Def.magazineSize}"
                : "weapon=none");

            return sb.ToString();
        }
    }

    /// <summary>
    /// Debug commands exposed to the Lua console as the global <c>sim</c> table:
    /// <c>sim:SetTickRate(120)</c>, <c>sim:GetTickRate()</c>.
    ///
    /// <para>Changing the tick rate is the one flag-flip experiment that is worth doing from a
    /// console rather than a rebuild, because the whole point is comparing the same scene at 30 and
    /// 120 Hz.</para>
    /// </summary>
    [LocalOnly]
    public sealed class DevConsoleSimCommands
    {
        private SimClock _clock;

        internal void Wire(SimClock clock)
        {
            _clock = clock;
        }

        /// <summary>
        /// Set the simulation tick rate. Unsupported rates snap to the nearest of
        /// 30/60/90/120, and the reply says so when a snap happened.
        /// </summary>
        public string SetTickRate(int ticksPerSecond)
        {
            if (_clock == null) return "[sim] SimClock not available.";

            _clock.TicksPerSecond = ticksPerSecond;

            string snapped = _clock.IsSupported
                ? string.Empty
                : $" (requested {_clock.RequestedTicksPerSecond}, snapped to nearest supported)";

            return $"[sim] Tick rate = {_clock.TicksPerSecond} Hz{snapped}.";
        }

        /// <summary>Current effective tick rate.</summary>
        public int GetTickRate()
        {
            return _clock?.TicksPerSecond ?? 0;
        }

        /// <summary>Report the clock's configuration.</summary>
        public string Info()
        {
            if (_clock == null) return "[sim] SimClock not available.";

            return $"[sim] ticksPerSecond={_clock.TicksPerSecond} simDt={_clock.SimDt:0.#####} " +
                   $"stepScale={_clock.StepScale:0.###} canonical={_clock.IsCanonical}";
        }
    }
}
