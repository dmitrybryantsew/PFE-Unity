using UnityEngine;
using PFE.Core;
using PFE.Entities.Units;
using PFE.Systems.Inventory;

namespace PFE.Entities.Player
{
    /// <summary>
    /// Walk-over auto-collection — AS3 <c>Loot.take()</c>'s automatic half (<c>fe/loc/Loot.as:281-326</c>),
    /// driven by the player's <c>isTake</c> window.
    ///
    /// <para><b>Why this is its own component.</b> Exactly the reason <see cref="PlayerManaTicker"/>
    /// documents: AS3 puts this in <c>UnitPlayer.step()</c>, the player's own frame, and the port has no
    /// player tick to hang it on — <c>UnitController.SimTick</c> is not virtual and returns early for a
    /// motor-driven unit, which the player is. So it is an <see cref="ISimTickable"/> at
    /// <see cref="SimTickOrder.PlayerMotor"/>, registered through the same
    /// <c>AttachSimulation(clock, loop)</c> idiom. <b>Registering is not optional</b>: an unregistered
    /// tickable silently never runs, which presents as "loot is never picked up" rather than as an
    /// error.</para>
    ///
    /// <para><b>The oracle's two gates, and why the flag alone is not enough.</b>
    /// <c>Loot.as:396-399</c> only calls <c>take()</c> when <c>this.auto &amp;&amp; this.auto2 ||
    /// this.actTake</c>, and the take branch inside (<c>:290</c>) additionally requires
    /// <c>World.w.gg.isTake &gt;= 1</c>. So a pickup needs BOTH <see cref="WorldItemPickup.AutoCollect"/>
    /// <i>and</i> an armed window. The window is what makes the feature read as "walk over it and it is
    /// picked up": <c>isTake</c> is set to 40 by <i>holding a horizontal direction</i>
    /// (<c>UnitPlayer.as:2551</c>, <c>:2570</c>) or by jumping (<c>:2685</c>), so while the player walks
    /// the window is pinned at 40 and both branches are live.</para>
    ///
    /// <para><b>The magnet is required, not decorative.</b> Ground loot sits 35 px below the player's body
    /// centre and the take band is ±20 px, so the take branch alone can never fire on a grounded item. The
    /// magnet (±50 px, active from <c>isTake &gt;= 20</c>) lifts it into range first. See
    /// <see cref="AutoPickupRule.BodyCentreY"/>.</para>
    ///
    /// <para><b>Not ported, and deliberately failing closed.</b> AS3's gate also includes
    /// <c>Item.checkAuto()</c> (<c>fe/serv/Item.as:385</c>) under <c>World.w.hardInv</c> — a
    /// weight/capacity predicate over the per-character <c>maxm*</c> caps. The port has
    /// <c>GameInventory.HardInventory</c> but its caps are fabricated and dead, so the capacity half of
    /// that predicate cannot be answered honestly. Rather than over-collect silently, this refuses to
    /// auto-take at all while <c>HardInventory</c> is on — the conservative direction — and says so once.
    /// Loot is then still collectable with the cursor, which is the path that never consulted the flag.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerAutoPickup : MonoBehaviour, ISimTickable
    {
        private PlayerInventory _inventory;
        private UnitController _unit;

        private SimLoop _loop;
        private bool _registered;
        private bool _warnedLegacy;
        private bool _warnedHardInventory;

        /// <summary>
        /// AS3 <c>UnitPlayer.isTake</c> (<c>UnitPlayer.as:177</c>). Counts down one per tick from
        /// <see cref="AutoPickupRule.TakeWindowTicks"/>; <c>&gt;= 1</c> enables the take branch and
        /// <c>&gt;= 20</c> the magnet.
        /// </summary>
        private int _takeWindow;

        /// <summary>Reused across ticks so the scan allocates nothing.</summary>
        private readonly System.Collections.Generic.List<WorldItemPickup> _taken =
            new System.Collections.Generic.List<WorldItemPickup>();

        /// <summary>The player's own step — loot collection is part of the player's frame in AS3.</summary>
        public int TickOrder => SimTickOrder.PlayerMotor;

        /// <summary>The window as the oracle would see it. Exposed for the F2 overlay's readout.</summary>
        public int TakeWindow => _takeWindow;

        /// <summary>Binds the collaborators. Called once by <see cref="PlayerController"/>.</summary>
        public void Construct(PlayerInventory inventory, UnitController unit)
        {
            _inventory = inventory;
            _unit = unit;
        }

        /// <summary>
        /// Arms the window to 40 — AS3's <c>this.isTake = 40</c>. Called by <see cref="PlayerController"/>
        /// while a horizontal direction is held, and on a jump press.
        ///
        /// <para><b>Set, never added to.</b> The oracle assigns rather than accumulates, so holding a
        /// direction pins the window at 40 instead of growing it without bound.</para>
        /// </summary>
        public void ArmTakeWindow() => _takeWindow = AutoPickupRule.TakeWindowTicks;

        /// <summary>Puts the collector on the sim clock. Safe to call twice.</summary>
        public void Attach(SimClock clock, SimLoop loop)
        {
            if (loop == null)
            {
                return;
            }

            _loop = loop;
            if (!_registered)
            {
                _loop.Register(this);
                _registered = true;
            }
        }

        public void SimTick(int tickIndex)
        {
            Step();
        }

        private void FixedUpdate()
        {
            if (_registered)
            {
                return;
            }

            if (!_warnedLegacy)
            {
                _warnedLegacy = true;
                Debug.LogWarning(
                    "[PlayerAutoPickup] No SimLoop attached, so loot collection runs on Unity's " +
                    "FixedUpdate instead of the sim clock. The 40-tick take window will then last a " +
                    "different wall-clock time than AS3's 40 frames.");
            }

            Step();
        }

        private void OnDestroy()
        {
            // Without this SimLoop keeps a reference to a dead MonoBehaviour and ticks it forever,
            // which presents as "the sim gets slower the longer the session runs".
            if (_loop != null && _registered)
            {
                _loop.Unregister(this);
                _registered = false;
            }
        }

        /// <summary>
        /// One tick: age the window, then collect or magnetise. All the arithmetic lives in
        /// <see cref="AutoPickupRule"/>; this method only gathers the numbers and applies the result.
        /// </summary>
        private void Step()
        {
            // AS3 decrements unconditionally and tests the value afterwards (UnitPlayer.as:897-899),
            // so a window of 1 is still live for this tick and reaches 0 for the next.
            if (_takeWindow > 0)
            {
                _takeWindow--;
            }

            if (!AutoPickupRule.TakeEngaged(_takeWindow))
            {
                return;
            }

            if (_inventory == null || _inventory.Inventory == null)
            {
                return;
            }

            if (_inventory.Inventory.HardInventory)
            {
                if (!_warnedHardInventory)
                {
                    _warnedHardInventory = true;
                    Debug.LogWarning(
                        "[PlayerAutoPickup] HardInventory is on, but the capacity half of AS3's " +
                        "Item.checkAuto() is not ported (the per-character maxm* caps are missing). " +
                        "Refusing to auto-take rather than over-collect; use the cursor to pick loot up.");
                }

                return;
            }

            Vector2 playerPos = transform.position;
            float bodyHeight = _unit != null ? _unit.SpriteSizePixels.y * AutoPickupRule.PixelsToUnits : 0f;
            float centreY = AutoPickupRule.BodyCentreY(playerPos.y, bodyHeight);
            Vector2 magnetTarget = new Vector2(playerPos.x, centreY);

            // Decide over the whole population first, then act: Interact() despawns, which mutates the
            // very list being walked (the shape lesson #78 records for IMGUI, and it applies here too).
            _taken.Clear();

            var live = WorldItemPickup.All;
            for (int i = 0; i < live.Count; i++)
            {
                WorldItemPickup pickup = live[i];
                if (pickup == null || !pickup.AutoCollect)
                {
                    continue;
                }

                Vector2 loot = pickup.Position;
                float dx = playerPos.x - loot.x;
                float dy = centreY - loot.y;

                if (AutoPickupRule.WithinTakeBand(dx, dy))
                {
                    _taken.Add(pickup);
                }
                else if (AutoPickupRule.MagnetEngaged(_takeWindow)
                         && AutoPickupRule.WithinMagnetBand(dx, dy))
                {
                    pickup.MagnetToward(magnetTarget);
                }
            }

            for (int i = 0; i < _taken.Count; i++)
            {
                WorldItemPickup pickup = _taken[i];
                if (pickup != null)
                {
                    // The same seam the cursor path uses, so a rejection leaves the pickup in the world
                    // and logs why — exactly as a failed manual take does.
                    pickup.Interact(gameObject);
                }
            }

            _taken.Clear();
        }
    }
}
