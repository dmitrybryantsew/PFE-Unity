using System;
using System.Collections.Generic;
using PFE.Data.Definitions.Campaign;
using PFE.Systems.Campaign;
using PFE.Systems.Inventory;
using UnityEngine;

namespace PFE.Systems.Map.Scripting
{
    /// <summary>
    /// Fires a land's <c>&lt;scr eve='take' item='…'&gt;</c> scripts — the port of AS3
    /// <c>Land.itemScripts</c> + <c>Invent.takeScript</c> (<c>Land.as:141-149</c>,
    /// <c>Invent.as:1554-1560</c>).
    ///
    /// <para><b>The oracle.</b> When a land is constructed it walks its own <c>&lt;scr&gt;</c> children
    /// and registers every one whose <c>@eve == "take"</c> into <c>land.itemScripts[@item]</c>. Then
    /// <c>Invent.take()</c> calls <c>takeScript(l.id)</c> from five sites, <i>each guarded by
    /// <c>this.items[l.id].kol == 0</c></i> — so the script runs the first time the player ever holds
    /// that item and not again (<c>:1102</c>, <c>:1132</c>, <c>:1192</c>, <c>:1213</c>, <c>:1227</c>).
    /// One of the five is the weapon branch, which is why this listens to weapons as well as
    /// items.</para>
    ///
    /// <para><b>Why it matters, and why it is not a nicety.</b> <c>collectHS</c> — the campaign's first
    /// quest — is granted from <b>exactly one place in the whole game</b>:
    /// <c>&lt;scr eve='take' item='col1'&gt;&lt;s act="quest" val="collectHS"/&gt;</c>
    /// (<c>GameData.as:44-46</c>). The <c>begin</c> land declares six of these scripts in total
    /// (<c>mont</c>, <c>screwdriver</c>, <c>rech</c>, <c>r32</c>, <c>s_arson</c>, <c>col1</c>); 14 exist
    /// across the 8 lands that use them. Without this path, taking the Cogwheel opened no quest and
    /// taking the screwdriver left <c>trDoor</c> shut.</para>
    ///
    /// <para><b>The seam is <see cref="GameInventory.OnItemAdded"/>, and it is the oracle's guard
    /// exactly.</b> That event is raised from <c>AddItem</c> only on the <c>else</c> branch — the item
    /// was not already in the dictionary (<c>GameInventory.cs:313-319</c>) — and an item at zero
    /// quantity is <i>removed</i> from the dictionary (<c>:376-381</c>). So "the event fired" and
    /// <c>kol == 0</c> are the same condition, not a lookalike. <see cref="GameInventory.OnWeaponAdded"/>
    /// is the same shape for the weapon half (<c>:409-432</c>).</para>
    ///
    /// <para><b>Re-taking re-fires, and that is the oracle.</b> AS3 drops the count to 0 without
    /// removing the row, so dropping an item and picking it up again re-runs the script; the port
    /// removes the row, so the event fires again. There is therefore deliberately <i>no</i>
    /// "already fired" latch — one would be a divergence dressed as a safety feature.</para>
    ///
    /// <para><b>Where the script runs.</b> AS3's <c>Script</c> is owned by the <i>land</i> and resolves
    /// <c>@targ</c> through <c>land.uidObjs</c> (<c>Script.as:224-231</c>). The port's runner is
    /// room-scoped, so this runs the script against the room the player is standing in when the item is
    /// taken — which is where the player is, and therefore where the targets of these scripts live. A
    /// target in a <i>different</i> room is reported by the runner's existing "no object in the room
    /// matches that uid or code" warning rather than silently dropped; that narrowing is the port's
    /// pre-existing <c>@targ</c> model and is documented on <c>IScriptObjectHost</c>. Note that the one
    /// script this class exists for — <c>col1</c> → <c>quest collectHS</c> — carries no <c>@targ</c> at
    /// all, so it is unaffected.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LandItemScriptRunner : MonoBehaviour
    {
        /// <summary>The player's inventory, resolved per use — the player can be rebuilt by a scene
        /// reload, and a captured <c>GameInventory</c> would then be a dead object that accepts
        /// nothing.</summary>
        private PlayerInventory _inventory;

        /// <summary>The map, for the room the player is in. A factory rather than a value because
        /// <c>PlayerController</c> is injected with the <c>LandMap</c> and this component is built
        /// during <c>Awake</c>, which Unity does not order against that injection.</summary>
        private Func<PFE.Systems.Map.LandMap> _landMap;

        private Action<string> _report;

        /// <summary>The inventory instance this is currently subscribed to. The subscription is moved
        /// when the instance changes rather than made once, so a respawn cannot leave the events wired
        /// to the previous player's bag.</summary>
        private GameInventory _subscribed;

        /// <summary>Runners this class registered with <see cref="ScriptSystem"/> and has not yet taken
        /// back off the clock. Pruned in <see cref="Update"/> as soon as each one has nothing in
        /// flight.</summary>
        private readonly List<ScriptRunner> _registered = new List<ScriptRunner>();

        /// <summary>Report keys already emitted, so a per-item miss does not become a per-frame
        /// log.</summary>
        private readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>How many item scripts this has actually started. For tests and the debug
        /// overlay.</summary>
        public int ScriptsRun { get; private set; }

        /// <summary>How many acquisitions had no authored script. Not an error — most items have
        /// none — but it is the readback that distinguishes "the table is empty" from "nothing was
        /// ever taken".</summary>
        public int AcquisitionsWithoutScript { get; private set; }

        public void Construct(PlayerInventory inventory, Func<PFE.Systems.Map.LandMap> landMap,
                              Action<string> report = null)
        {
            _inventory = inventory;
            _landMap = landMap;
            _report = report ?? (message => Debug.LogWarning(message));
        }

        /// <summary>
        /// Keeps the subscription pointed at the live inventory. Cheap and idempotent: it compares the
        /// instance and returns, so it is safe on every frame.
        /// </summary>
        private void Update()
        {
            EnsureSubscribed();
            PruneFinishedRunners();
        }

        private void EnsureSubscribed()
        {
            GameInventory current = _inventory != null ? _inventory.Inventory : null;
            if (ReferenceEquals(current, _subscribed)) return;

            if (_subscribed != null)
            {
                _subscribed.OnItemAdded -= OnItemAdded;
                _subscribed.OnWeaponAdded -= OnWeaponAdded;
            }

            _subscribed = current;

            if (_subscribed != null)
            {
                _subscribed.OnItemAdded += OnItemAdded;
                _subscribed.OnWeaponAdded += OnWeaponAdded;
            }
        }

        private void OnDestroy()
        {
            if (_subscribed != null)
            {
                _subscribed.OnItemAdded -= OnItemAdded;
                _subscribed.OnWeaponAdded -= OnWeaponAdded;
                _subscribed = null;
            }

            ScriptSystem system = ScriptSystem.Current;
            if (system != null)
            {
                for (int i = 0; i < _registered.Count; i++) system.Unregister(_registered[i]);
            }

            _registered.Clear();
        }

        private void OnItemAdded(GameItemInstance item)
        {
            // The id the oracle keys `itemScripts` on is the ITEM id (`Item.id`), which is the
            // definition's id — not the instance's. A null definition means the row could not be
            // resolved, which `GameInventory.AddItem` refuses before reaching here.
            string itemId = item?.Definition != null ? item.Definition.itemId : null;
            if (!TryRunFor(itemId)) AcquisitionsWithoutScript++;
        }

        private void OnWeaponAdded(GameWeaponInstance weapon)
        {
            // `Invent.as:1102` — the weapon branch of the same five takeScript call sites. The port's
            // weapon ids live in ContentType.Weapon, and `GameWeaponInstance.ItemId` is the accessor
            // that answers for either construction path.
            string weaponId = weapon != null ? weapon.ItemId : null;
            if (!TryRunFor(weaponId)) AcquisitionsWithoutScript++;
        }

        /// <summary>
        /// Starts the current land's item script for <paramref name="itemId"/>, if it has one.
        ///
        /// <para>Public so a fixture can drive it without an inventory, and so the debug console can
        /// replay a take. Returns <c>false</c> when nothing was started — which is the common case and
        /// is not an error: 14 items in the whole game have a take script and roughly 500 do
        /// not.</para>
        /// </summary>
        public bool TryRunFor(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return false;

            LandItemScript authored = FindItemScript(itemId);
            if (authored == null || authored.actions == null || authored.actions.Count == 0) return false;

            // The land's script is converted with the same adapter an authored `<scr id>` uses, so the
            // two cannot drift on the `n`/`opt1` mapping. See ScriptCatalogHost.Convert.
            MapObjectScriptData script = ScriptCatalogHost.Convert(authored.actions);
            if (script == null || script.actions.Count == 0) return false;

            ScriptSystem system = ScriptSystem.Current;
            if (system == null)
            {
                ReportOnce("(script system)",
                    $"[LandItemScriptRunner] taking '{itemId}' should start the land's take script, but " +
                    "no ScriptSystem exists, so it did not run. In a correctly booted game the container " +
                    "sets it in GameLifetimeScope.Awake.");
                return false;
            }

            PFE.Systems.Map.LandMap map = _landMap?.Invoke();
            RoomInstance room = map != null ? map.currentRoom : null;
            if (room == null)
            {
                ReportOnce("(no room)",
                    $"[LandItemScriptRunner] taking '{itemId}' should start the land's take script, but " +
                    "there is no current room, so there is nothing to run it against.");
                return false;
            }

            // The room's own `@targ` host. Null is legal — a room whose visuals were never built — and
            // the runner then reports each `@targ` action rather than resolving it.
            IScriptObjectHost objects = system.ObjectHostFor(room);

            // A runner per start, registered only while it has something in flight. See
            // PruneFinishedRunners for the other half: a script with no `@t` and no blocking command
            // runs to completion inside Run() and never needs the clock at all.
            ScriptRunner runner = system.CreateRunner(objects);
            runner.Run(room, script);

            if (runner.RunningCount > 0)
            {
                system.Register(runner);
                _registered.Add(runner);
            }

            ScriptsRun++;
            Debug.Log($"[LandItemScriptRunner] item '{itemId}' started the land's take script " +
                      $"({script.actions.Count} action(s)) in room '{room.id}'.");
            return true;
        }

        /// <summary>
        /// Takes every runner this class registered back off the clock once it has nothing in flight.
        ///
        /// <para><b>Why pruning is needed at all.</b> A registered runner is stepped for the life of
        /// the <c>ScriptSystem</c>, so a runner per item taken would accumulate one per take — each
        /// doing nothing, but each visited on every sim tick. A runner that finished is identified by
        /// <see cref="ScriptRunner.RunningCount"/> being zero: <c>SimTick</c> removes a script from its
        /// list when it completes, and a just-started one is added synchronously by <c>Run</c>, so the
        /// count cannot be zero in the window between starting and the first step.</para>
        /// </summary>
        private void PruneFinishedRunners()
        {
            if (_registered.Count == 0) return;

            ScriptSystem system = ScriptSystem.Current;
            for (int i = _registered.Count - 1; i >= 0; i--)
            {
                ScriptRunner runner = _registered[i];
                if (runner == null)
                {
                    _registered.RemoveAt(i);
                    continue;
                }

                if (runner.RunningCount > 0) continue;

                system?.Unregister(runner);
                _registered.RemoveAt(i);
            }
        }

        /// <summary>
        /// The authored script for <paramref name="itemId"/> in the land the player is currently in, or
        /// <c>null</c>. The land is read per call for the same reason <c>AreaTriggerSystem.LandHost</c>
        /// is: the world is built before the campaign is guaranteed to exist.
        /// </summary>
        private LandItemScript FindItemScript(string itemId)
        {
            CampaignManager campaign = CampaignManager.Current;
            // `CurrentValue`, not `Value` — R3's ReadOnlyReactiveProperty exposes the latest emission
            // under that name (the same accessor DevConsoleCommands.cs:471 uses for ArmourId).
            LandDefinition land = campaign?.CurrentLand?.CurrentValue;
            if (land?.itemScripts == null) return null;

            for (int i = 0; i < land.itemScripts.Count; i++)
            {
                LandItemScript candidate = land.itemScripts[i];
                if (candidate != null && string.Equals(candidate.itemId, itemId, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            return null;
        }

        private void ReportOnce(string key, string message)
        {
            if (_reported.Add(key)) _report(message);
        }
    }
}
