using System;
using System.Text;
using MessagePipe;
using PFE.Core.Messages;
using PFE.Data;
using PFE.Data.Definitions;
using PFE.Entities.Player;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Inventory;
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

        // ── Telekinesis / teleport Q path ─────────────────────────────────────
        //
        // These belong on the `player` object and not a separate one: the grab gate, the cursor, the
        // stats and the teleport charge are all the player's own state. (The collider commands are
        // separate because their subject is the world and the HUD, not the player.)

        /// <summary>
        /// Turn the <c>[tele]</c> trace on or off. The flag lives in <see cref="PfeDebugSettings"/>, so
        /// this command and the Inspector drive one value.
        /// </summary>
        public string TeleTrace(bool enabled)
        {
            PfeDebugSettings settings = DebugOverlays.Settings;
            if (settings == null)
            {
                return "[tele] No PfeDebugSettings asset found under Resources, so the trace cannot be switched on.";
            }

            settings.LogTelekinesisTrace = enabled;
            return enabled
                ? "[tele] Trace ON. Press Q and watch the Console -- every line starts with '[tele]'. " +
                  "Or run 'tele probe' to see the current state without pressing anything."
                : "[tele] Trace OFF.";
        }

        /// <summary>
        /// Report what the grab path sees right now, in the order it tests it, without pressing
        /// anything. This is the command to reach for when pressing Q does nothing: the last line names
        /// the verdict, so the report identifies the broken link itself.
        /// </summary>
        public string TeleProbe()
        {
            var player = Player();
            if (player == null)
            {
                return "[tele] No PlayerController in the scene.";
            }

            PlayerTelekinesisController telekinesis = player.Telekinesis;
            if (telekinesis == null)
            {
                return "[tele] PlayerController has no PlayerTelekinesisController. It is created in " +
                       "PlayerController.Awake via AddComponent, so this means Awake never ran.";
            }

            string report = telekinesis.Probe();
            Debug.Log(report);
            return report;
        }

        /// <summary>Whether the <c>[tele]</c> trace is currently on.</summary>
        public string TeleStatus()
        {
            PfeDebugSettings settings = DebugOverlays.Settings;
            bool on = settings != null && settings.LogTelekinesisTrace;
            return $"[tele] trace is {(on ? "ON" : "OFF")}. `tele on` | `tele off` | `tele probe`";
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

        /// <summary>Toggle or open the LittlePip character/loadout debug editor overlay.</summary>
        public string Editor()
        {
            if (PlayerDebugEditorOverlay.Instance != null)
            {
                PlayerDebugEditorOverlay.Instance.IsOpen = !PlayerDebugEditorOverlay.Instance.IsOpen;
                return $"[editor] Character loadout editor is now {(PlayerDebugEditorOverlay.Instance.IsOpen ? "OPEN" : "CLOSED")}. (Hotkey: F2)";
            }
            return "[editor] PlayerDebugEditorOverlay instance not found.";
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

        // ── Armour ────────────────────────────────────────────────────────────
        //
        // These exist to make armour reachable *before* the inventory is. Nothing in production
        // constructs a GameInventory, assigns its ArmorDefinitionResolver, or calls EquipArmour, so
        // without a console entry point the entire armour feature — the 35 definitions, the combat
        // projection, the HUD bar and the character sprite — is exercised only by tests.
        //
        // They deliberately drive the same path the interaction layer will: GameDatabase lookup ->
        // GameArmorInstance -> UnitStats.EquipArmour, which is what publishes ArmourId and therefore
        // what moves the sprite. So a console equip is a real end-to-end test of the chain, not a
        // shortcut around it.

        /// <summary>Equip armour by content id — <c>player:EquipArmour("metal")</c>.</summary>
        public string EquipArmour(string id)
        {
            var player = Player();
            if (player?.Stats == null) return "[player] No player in scene.";

            if (string.IsNullOrWhiteSpace(id))
                return "[player] Usage: player:EquipArmour(\"metal\") — see player:ArmourIds().";

            ItemDefinition definition = _database != null ? _database.GetItem(id) : null;
            if (definition == null)
            {
                return $"[player] No item definition '{id}'. Armour is created by " +
                       "'PFE/Data/Import Armour from AllData.as'; until that menu item has been run, " +
                       "Resources/Armor is empty and every armour id is unknown. Try player:ArmourIds().";
            }

            if (!definition.IsArmour)
            {
                return $"[player] '{id}' is not armour (no per-level table), so equipping it would give " +
                       "a plate with no ratings. Use player:GiveWeapon for weapons.";
            }

            var item = new GameArmorInstance(definition);
            player.Stats.EquipArmour(item);

            return $"[player] Equipped '{id}' — integrity {item.CurrentHealth:0.#}/{item.MaxHealth:0.#}, " +
                   $"tip={definition.armorTip}, level={item.Level}, mane hidden={item.HideMane}.";
        }

        /// <summary>Take the equipped armour off — <c>player:UnequipArmour()</c>.</summary>
        public string UnequipArmour()
        {
            var player = Player();
            if (player?.Stats == null) return "[player] No player in scene.";

            string was = player.Stats.ArmourId.CurrentValue;
            player.Stats.UnequipArmour();

            return string.IsNullOrEmpty(was)
                ? "[player] Nothing was equipped."
                : $"[player] Unequipped '{was}'.";
        }

        /// <summary>List the armour ids the registry actually knows — <c>player:ArmourIds()</c>.</summary>
        public string ArmourIds()
        {
            if (_database == null) return "[player] No GameDatabase available.";

            var sb = new StringBuilder();
            int count = 0;

            foreach (string id in _database.GetAllItemIDs())
            {
                ItemDefinition definition = _database.GetItem(id);
                if (definition == null || !definition.IsArmour) continue;

                if (count > 0) sb.Append(", ");
                sb.Append(id);
                count++;
            }

            if (count == 0)
            {
                return "[player] The registry holds no armour. Run 'PFE/Data/Import Armour from AllData.as' " +
                       "and re-enter play mode — the registry is populated once, at boot.";
            }

            return $"[player] {count} armour id(s): {sb}";
        }

        /// <summary>Report the equipped armour — <c>player:Armour()</c>.</summary>
        public string Armour()
        {
            var player = Player();
            if (player?.Stats == null) return "[player] No player in scene.";

            if (!player.Stats.HasArmour.CurrentValue)
                return "[player] No armour equipped.";

            IArmourItem item = player.Stats.ArmourItem;
            return $"[player] Armour '{player.Stats.ArmourId.CurrentValue}' — " +
                   $"condition {player.Stats.ArmourIntegrity.CurrentValue * 100f:0.#}%, " +
                   $"effective physical {player.Stats.armour.EffectivePhysicalRating:0.##}, " +
                   $"effective energy {player.Stats.armour.EffectiveEnergyRating:0.##}" +
                   (item is GameArmorInstance instance
                       ? $", item hp {instance.CurrentHealth:0.#}/{instance.MaxHealth:0.#} (level {instance.Level})"
                       : "");
        }

        /// <summary>
        /// Wear the equipped armour by <paramref name="amount"/> — <c>player:WearArmour(150)</c>.
        ///
        /// <para>Exists because the break behaviour is the one part of the chain that is invisible
        /// until it happens: a hit that empties the plate unequips it, which drops <c>ArmourId</c> and
        /// takes the sprite off. There is no other way to see that without a fight.</para>
        /// </summary>
        public string WearArmour(float amount)
        {
            var player = Player();
            if (player?.Stats == null) return "[player] No player in scene.";

            if (!player.Stats.HasArmour.CurrentValue) return "[player] No armour equipped.";
            if (amount <= 0f) return "[player] Usage: player:WearArmour(150) — amount must be positive.";

            bool broke = player.Stats.ApplyDamage(new DamageOutcome(
                hpDamage: 0f,
                armourIntegrityDamage: amount));

            if (broke)
            {
                return $"[player] Wore {amount:0.#} — the armour BROKE and was unequipped " +
                       "(AS3 Armor.damage() -> changeArmor(\"off\")). Its sprite should now be gone.";
            }

            return $"[player] Wore {amount:0.#} — condition now " +
                   $"{player.Stats.ArmourIntegrity.CurrentValue * 100f:0.#}%.";
        }

        /// <summary>Report the player's position, health, equipped weapon and armour.</summary>
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

            string armourId = player.Stats.ArmourId.CurrentValue;
            sb.Append(string.IsNullOrEmpty(armourId)
                ? " armour=none"
                : $" armour='{armourId}' condition={player.Stats.ArmourIntegrity.CurrentValue * 100f:0.#}%");

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
