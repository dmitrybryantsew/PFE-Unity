using System;
using PFE.Core.Rng;
using PFE.Data.Definitions;
using PFE.Systems.Interaction;
using PFE.Systems.Inventory;
using PFE.Systems.Loot;
using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Presenter component for in-room <b>container</b> props — the port of AS3 <c>fe.serv.Interact</c>'s
    /// container half: <c>actOsn()</c> → <c>loot()</c> (<c>Interact.as:1265-1328</c>, <c>:1960-2025</c>).
    ///
    /// <para><b>A prop is a container iff its resolved <c>cont</c> is non-empty and not <c>"empty"</c></b>
    /// — <see cref="ContainerRule"/>, never a family enum or an id list. AS3 has no container class;
    /// <c>cont != null</c> is the only switch (<c>Interact.as:1285-1288</c>). The resolved value is read
    /// placement-first (<c>:332-335</c>) then definition (<c>:210-213</c>), which
    /// <see cref="ContainerRule.ResolveCont"/> owns.</para>
    ///
    /// <para><b>Modelled on <see cref="DoorPropPresenter"/>, deliberately.</b> That class is the worked
    /// precedent for every seam here — admission (<c>RoomObjectVisualManager</c>), the trigger collider
    /// the cursor probe needs, <c>CanInteract</c>'s reach guard, the per-frame press guard, and
    /// <c>IHoldInteractable</c>. The only differences are the verb (roll instead of toggle) and the end
    /// state (<c>runtimeState.lootState</c> instead of <c>isOpen</c>).</para>
    ///
    /// <para><b>Opening ejects; there is no loot window.</b> AS3's container interaction spills the
    /// rolled items onto the floor as <c>fe.loc.Loot</c> props and the box becomes permanently dead
    /// (<c>setAct("loot", n)</c>, <c>:834-844</c>). The port does the same through
    /// <see cref="WorldItemPickup.Spawn"/> — a grid-loot UI would be something the oracle does not
    /// have.</para>
    /// </summary>
    [RequireComponent(typeof(BoxCollider2D))]
    public sealed class ContainerPropPresenter : MonoBehaviour, IInteractable, IHoldInteractable
    {
        /// <summary>How close the player must be — AS3 <c>World.w.actionDist</c>, shared with the
        /// player's own target search so the two cannot disagree.</summary>
        public const float ActionReach = WorldConstants.ACTION_REACH;

        /// <summary>
        /// Half-extent of the eject scatter, in world units. AS3's loot <i>falls</i> and settles; the
        /// port has no falling-loot model (that needs <c>Loot.checkShelf</c> against a physics layer
        /// that does not exist yet), so the burst is placed with a small deterministic scatter instead.
        /// Recorded rather than silently shipped as a static pile — see
        /// <c>docs/DeviceInteractionAndLoot/04_IMPLEMENTATION_PLAN.md</c> D7 Problem B.
        /// </summary>
        private const float ScatterRadius = 0.3f;

        private RoomInstance _room;
        private ObjectInstance _objectInstance;
        private MapObjectVisualDefinition _visual;
        private SpriteRenderer _renderer;
        private BoxCollider2D _triggerCollider;

        private int _lastInteractFrame = -1;

        /// <summary>
        /// The resolved <c>cont</c> for this prop — <see cref="ContainerRule.ResolveCont"/> of the
        /// placement's value over the definition's. Cached because it cannot change while the room lives.
        /// </summary>
        public string ResolvedCont { get; private set; } = string.Empty;

        public RoomInstance Room => _room;
        public ObjectInstance ObjectInstance => _objectInstance;

        /// <summary>Whether this prop is a container at all — AS3 <c>cont != null</c>.</summary>
        public bool IsContainer => ContainerRule.IsContainer(ResolvedCont);

        /// <summary>
        /// Whether this container has already been emptied — AS3 <c>cont == "empty"</c>, which the port
        /// models as <c>runtimeState.lootState &gt; 0</c> (<c>setAct("loot", n)</c> writes both,
        /// <c>Interact.as:840</c>), because <c>lootState</c> is the value that is serialised
        /// (<c>RoomStateSnapshot</c>) and the value the frame chooser already reads
        /// (<c>RoomObjectVisualManager.ResolveFrameIndex</c>).
        /// </summary>
        public bool IsLooted => _objectInstance?.runtimeState != null && _objectInstance.runtimeState.lootState > 0;

        /// <summary>
        /// The HUD prompt. "Open" while there is something to open, empty once looted — AS3
        /// <c>update()</c> (<c>Interact.as:672</c>, <c>:777-780</c>). The "empty" state string is a
        /// separate presentation concern (D12), not this label.
        /// </summary>
        public string ActionText => Admits(IsContainer, IsLooted) ? "Open" : string.Empty;

        /// <summary>Frames the action key must be held — AS3 <c>Interact.t_action</c>, authored as
        /// <c>time</c>. Almost every container authors none, so this is 0 and it acts on the press.</summary>
        public int HoldFrames => _objectInstance != null ? _objectInstance.GetHoldFrames() : 0;

        /// <summary>The container's own world position — the hold's range guard and the eject origin.</summary>
        public Vector3 WorldPosition => transform.position;

        public void Initialize(
            RoomInstance room,
            ObjectInstance obj,
            MapObjectVisualDefinition visual,
            SpriteRenderer renderer)
        {
            _room = room;
            _objectInstance = obj;
            _visual = visual;
            _renderer = renderer;

            ResolvedCont = ContainerRule.ResolveCont(
                obj?.GetAttribute("cont", null),
                obj?.definition != null ? obj.definition.containerId : null);

            _triggerCollider = GetComponent<BoxCollider2D>();
            if (_triggerCollider != null)
            {
                _triggerCollider.isTrigger = true;
                ConfigureCollider();
            }
        }

        /// <summary>
        /// Whether the player may work this container. The shape test (is it a container, is it already
        /// looted) is split out so a test can assert it without a scene, exactly as
        /// <c>DoorPropPresenter.AdmitsPlayerInteraction</c> is.
        /// </summary>
        public static bool Admits(bool isContainer, bool isLooted) => isContainer && !isLooted;

        public bool CanInteract(GameObject user)
        {
            if (!Admits(IsContainer, IsLooted)) return false;
            if (user == null) return false;

            float dist = Vector2.Distance(transform.position, user.transform.position);
            return dist <= ActionReach;
        }

        /// <summary>
        /// Open the container: roll its table, eject the results, and mark it looted. Called by the
        /// player's interaction path — this component reads no input (same ownership split as the door).
        /// </summary>
        public void Interact(GameObject user)
        {
            if (!Admits(IsContainer, IsLooted)) return;

            // Guarded per frame for the same reason the door is: the press can arrive twice (the
            // MessagePipe subscription and the direct poll), and a second roll on the same frame would
            // double the loot.
            if (Time.frameCount == _lastInteractFrame) return;
            _lastInteractFrame = Time.frameCount;

            LootContext ctx = LootRuntime.CreateContext(ResolvedCont);
            if (ctx == null)
            {
                // LootRuntime has already logged why. Do NOT mark the container looted — an unready
                // runtime must not consume the container.
                return;
            }

            ApplyRoomDifficulty(ctx);

            bool anyItems = LootRoller.RollContainer(ResolvedCont, ctx);

            int spawned = SpillRolls(user, ctx);
            ReportUnmatched(ctx);

            if (!anyItems || spawned == 0)
            {
                // AS3 `itsEmpty` (Interact.as:2020-2023) — reported, not silent.
                Debug.Log($"[ContainerPropPresenter] '{ResolvedCont}' yielded nothing at " +
                          $"{transform.position.x:0.##},{transform.position.y:0.##} (itsEmpty).");
            }

            // AS3 setAct("loot", 1): saveLoot = 1, active = false, cont = "empty".
            //
            // `active = false` is deliberately NOT ported here. In the port `ObjectInstance.isActive`
            // is also the renderer's visibility flag (RoomObjectVisualManager.UpdatePresenter sets
            // `renderer.enabled = ... && obj.isActive`), so writing it would make the opened crate
            // vanish instead of showing its looted frame — the opposite of what AS3's
            // `setVisState("open")` does. The interaction gate is `lootState`, which is what
            // `ResolveFrameIndex` already reads for the looted sprite.
            _objectInstance.runtimeState.lootState = 1;
        }

        /// <summary>
        /// Fill the room-difficulty inputs the tables scale off, from the room's
        /// <see cref="RoomDifficulty"/>.
        ///
        /// <para><c>LocDifLevel</c> is AS3 <c>loc.locDifLevel</c>, which the port stores as
        /// <see cref="RoomDifficulty.baseDifficulty"/> — the field <c>LocationDifficultyPlanner.Apply</c>
        /// writes from <c>Land.setLocDif</c> (<c>Land.as:982-987</c>). The other three derived values
        /// (<c>lockLevel</c>, <c>mechLevel</c>, <c>weaponLevel</c>) are written by the same call, so the
        /// tables scale off real numbers rather than a hard-coded 0.</para>
        ///
        /// <para><b>Still not wired:</b> <c>GameStage</c>, <c>LootLimit</c>, the perk flags, the per-run
        /// limit store and the room environment (<c>biom</c>/<c>itemsTip</c>). Each defaults to the
        /// oracle's "absent" behaviour, so a stage-gated roll passes (as AS3 does at <c>gameStage 0</c>)
        /// and a limit is not enforced. Recorded, not hidden — see D10.</para>
        /// </summary>
        private void ApplyRoomDifficulty(LootContext ctx)
        {
            RoomDifficulty difficulty = _room?.difficulty;
            ctx.LocDifLevel = difficulty != null ? Mathf.RoundToInt(difficulty.baseDifficulty) : 0;
            ctx.WeaponLevel = difficulty != null ? difficulty.weaponLevel : 1f;

            // Interact.as:2013 — `this.prize ? this.allDif : 50`. A prize container passes its own lock
            // difficulty; an ordinary one passes the literal 50, not 0.
            ctx.AllDif = 50f;
            ctx.LootBroken = false;
        }

        /// <summary>
        /// Eject every rolled item as a world pickup near the container. Returns how many were actually
        /// spawned. A roll whose id resolves to no <c>ItemDefinition</c> is <b>named in a warning</b>,
        /// never dropped silently.
        /// </summary>
        private int SpillRolls(GameObject user, LootContext ctx)
        {
            PlayerInventory inventory = user != null ? user.GetComponent<PlayerInventory>() : null;
            if (inventory == null)
            {
                Debug.LogWarning("[ContainerPropPresenter] the interactor has no PlayerInventory, so " +
                                 $"'{ResolvedCont}' rolled {ctx.Results.Count} item(s) that cannot be resolved or spawned.");
                return 0;
            }

            IRngService rng = ctx.Rng;
            int spawned = 0;

            for (int i = 0; i < ctx.Results.Count; i++)
            {
                LootRoll roll = ctx.Results[i];
                ItemDefinition definition = inventory.ResolveItem(roll.ItemId);
                if (definition == null)
                {
                    // Weapons and armour are separate content types and have no world-pickup path yet, so
                    // a weapon roll lands here. Reported with the id so it is a visible gap, not a
                    // silently thinner table.
                    Debug.LogWarning($"[ContainerPropPresenter] '{ResolvedCont}' rolled '{roll.ItemId}' " +
                                     "(tip '" + roll.Tip + "') but no ItemDefinition resolves it — it was " +
                                     "not spawned. Weapons/armour have no world-pickup path yet.");
                    continue;
                }

                Vector3 where = transform.position + Scatter(rng);
                if (WorldItemPickup.Spawn(definition, roll.Quantity, where, autoCollect: true) != null)
                {
                    spawned++;
                }
            }

            return spawned;
        }

        /// <summary>
        /// A small deterministic offset for one ejected item — the stand-in for AS3's fall/bounce. Uses
        /// the injected loot RNG so a seeded test can predict it.
        /// </summary>
        private static Vector3 Scatter(IRngService rng)
        {
            if (rng == null) return Vector3.zero;
            float dx = (rng.NextFloat() * 2f - 1f) * ScatterRadius;
            float dy = (rng.NextFloat() * 2f - 1f) * ScatterRadius;
            return new Vector3(dx, dy, 0f);
        }

        /// <summary>
        /// Report any table key that matched no branch. AS3 is silent (the <c>if/else if</c> chain falls
        /// through), which is how <c>cont="*"</c> produced an empty container unnoticed for years (Q1).
        /// </summary>
        private void ReportUnmatched(LootContext ctx)
        {
            for (int i = 0; i < ctx.UnmatchedKeys.Count; i++)
            {
                Debug.LogWarning($"[ContainerPropPresenter] container key '{ctx.UnmatchedKeys[i]}' matched " +
                                 "no loot table — nothing was rolled. (AS3 is silent here; see Q1.)");
            }
        }

        private void ConfigureCollider()
        {
            if (_triggerCollider == null) return;

            if (!Admits(IsContainer, IsLooted))
            {
                // Not a container (or already empty): leave no target box, so the cursor probe cannot
                // select it and pressing E does nothing.
                _triggerCollider.enabled = false;
                return;
            }

            _triggerCollider.enabled = true;
            GetVisualBounds(out Vector2 size, out Vector2 offset);
            _triggerCollider.size = size;
            _triggerCollider.offset = offset;
        }

        private void GetVisualBounds(out Vector2 size, out Vector2 offset)
        {
            if (_renderer != null && _renderer.sprite != null)
            {
                size = (Vector2)_renderer.sprite.bounds.size;
                offset = (Vector2)_renderer.sprite.bounds.center;
                return;
            }

            float w = _visual != null ? Mathf.Max(0.2f, _visual.pixelSize.x * 0.01f) : 0.4f;
            float h = _visual != null ? Mathf.Max(0.2f, _visual.pixelSize.y * 0.01f) : 0.4f;
            size = new Vector2(w, h);
            offset = Vector2.zero;
        }
    }
}
