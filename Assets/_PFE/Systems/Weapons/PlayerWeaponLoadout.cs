using System.Collections.Generic;
using R3;
using UnityEngine;
using VContainer;
using VContainer.Unity;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Weapons;
using PFE.Systems.Audio;
using PFE.Systems.Combat;
using PFE.Systems.Inventory;
using PFE.Systems.Magic;
using PFE.Systems.Weapons.Controllers;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// MonoBehaviour on the player. Owns the active weapon controller and
    /// drives it every FixedUpdate. Replaces PlayerController's weapon
    /// ownership so that PlayerController stays focused on movement and input routing.
    ///
    /// Responsibilities:
    ///   - Create the correct IWeaponController via WeaponControllerFactory on equip
    ///   - Dispose the previous controller when switching weapons
    ///   - Call controller.Tick() each FixedUpdate with mount positions and aim target
    ///   - Collect ShotPlans and forward them to ProjectileSpawner and WeaponPresenter
    ///   - Expose reactive state (ammo, reload) for HUD binding
    ///
    /// Setup:
    ///   1. Add this component to the player root GameObject alongside PlayerController.
    ///   2. Assign _startingWeaponDef in the Inspector (any pistol/rifle to start).
    ///   3. Assign _mounts (WeaponMounts component on the same or child GameObject).
    ///   4. Assign _weaponPresenter (WeaponPresenter component on the weapon child GameObject).
    ///   5. Assign _projectileSpawner (ProjectileSpawner component on the player or weapon child).
    ///   6. Assign _meleeHitVolume (MeleeHitVolume component on a trigger child of the weapon object).
    ///      Leave null if no melee weapons are used.
    ///
    /// Execution order: -100 so it runs before CharacterAnimationDriver (-50)
    /// but after PlayerLocomotionController (-200).
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class PlayerWeaponLoadout : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Starting weapon")]
        [SerializeField]
        [Tooltip("WeaponDefinition asset equipped at game start. Assign any pistol/rifle for testing.")]
        private WeaponDefinition _startingWeaponDef;

        [Header("Bare hooves")]
        [SerializeField]
        [Tooltip("AS3's `punch` weapon row (WKick). Leave empty to load Resources/Weapons/punch, " +
                 "which is what the game ships.")]
        private WeaponDefinition _punchWeaponDef;

        [Header("References")]
        [SerializeField]
        [Tooltip("WeaponMounts component providing hold point and horn point positions.")]
        private WeaponMounts _mounts;

        [SerializeField]
        [Tooltip("WeaponPresenter drives the weapon sprite. Assign the component on the weapon child.")]
        private WeaponPresenter _weaponPresenter;

        [SerializeField]
        [Tooltip("ProjectileSpawner converts ShotPlans into scene GameObjects.")]
        private ProjectileSpawner _projectileSpawner;

        [SerializeField]
        [Tooltip("MeleeHitVolume on a trigger child of the weapon GameObject. " +
                 "Assigned to MeleeWeaponController.HitVolume on equip. Leave null if not using melee.")]
        private MeleeHitVolume _meleeHitVolume;

        /// <summary>
        /// The rig that draws the player, used for one thing: reading the head's <c>konec</c> part,
        /// which is where magic leaves from (<c>UnitPlayer.as:3563</c>). Auto-resolved in
        /// <see cref="Awake"/>; null is legal and means "no rig", which is AS3's <c>catch</c> branch.
        ///
        /// <para>A component reference rather than a Transform because the point is not a node: the
        /// part is invisible marker art with no GameObject, and its position changes with the
        /// animation — see <see cref="PFE.Character.CharacterSpriteAssembler.TryGetPartWorldPosition"/>.
        /// </para>
        /// </summary>
        [SerializeField]
        [Tooltip("Character rig to read the magic horn point from. Auto-found on this object or a child.")]
        private PFE.Character.CharacterSpriteAssembler _characterAssembler;

        [Header("Aim")]
        [SerializeField]
        [Tooltip("World-space aim target this frame. Set each Update by PlayerController.HandleAiming().")]
        private Vector2 _aimTarget;

        // ── Injected dependencies ─────────────────────────────────────────────

        private IProjectileFactory _projectileFactory;
        private IObjectResolver    _resolver;
        private PfeDebugSettings   _debugSettings;
        private ISoundService      _soundService;
        private PFE.Core.Rng.IRngService _rng;

        /// <summary>
        /// Ammo source (player inventory). Assign before equipping when inventory is live.
        /// Leave null for training/infinite-ammo mode.
        /// </summary>
        public IAmmoSource AmmoSource
        {
            get => _ammoSource;
            set
            {
                _ammoSource = value;
                // Rebuild factory so future Equip() calls pick up the source.
                RebuildFactory();
            }
        }
        private IAmmoSource _ammoSource;

        /// <summary>
        /// Resolves a fired round's <b>id</b> to its ballistics row, so the round's damage/pierce/
        /// armour/knock/fire/det/type terms reach the shot.
        ///
        /// <para><b>Why it is built here and not injected as an interface.</b> The only production
        /// implementation is <see cref="ContentRegistryAmmoResolver"/>, which wraps the DI-singleton
        /// <see cref="ContentRegistry"/>. Registering the resolver itself would mean a second
        /// registration that has to be kept in step with the registry's; constructing it from the
        /// registry the container already owns keeps one source of truth. Null — before
        /// <see cref="Construct"/> runs, or in a plain-<c>Awake</c> test rig — means every shot
        /// resolves no round, which is AS3's failure branch and leaves the weapon's own numbers
        /// untouched.</para>
        /// </summary>
        private IAmmoResolver _ammoResolver;

        /// <summary>
        /// The owner's RPG multipliers — the <c>Pers</c> fields AS3 copies onto the weapon in
        /// <c>Weapon.setPers</c> (<c>Weapon.as:963-1050</c>): reload speed, recoil, jam chance and
        /// the energy-ammo recycle chance.
        ///
        /// <para>Resolved automatically on <see cref="Equip"/> from the parent
        /// <c>UnitController</c>'s <c>CharacterStats</c>, so the player gets them without any
        /// Inspector wiring. Left null (no CharacterStats in the hierarchy) the weapon falls back to
        /// the AS3 declaration defaults 1/1/1/0, which is exactly what AS3 does for a unit whose
        /// <c>Pers</c> was never set up.</para>
        /// </summary>
        public IWeaponStatSource WeaponStatSource
        {
            get => _weaponStatSource;
            set
            {
                _weaponStatSource = value;
                RebuildFactory();
            }
        }
        private IWeaponStatSource _weaponStatSource;

        /// <summary>
        /// Rebuild the controller factory so the next <see cref="Equip"/> sees the current
        /// dependencies. Every construction site goes through here: the three call sites used to
        /// each spell the argument list out, and the <c>Awake</c> copy had silently drifted — it
        /// passed no RNG, so a weapon equipped before <c>Construct</c> ran drew from an unseeded
        /// stream of its own.
        /// </summary>
        private void RebuildFactory()
        {
            // The stat source doubles as the mana source: CharacterStats implements both, and a test
            // double that only supplies multipliers simply yields null mana ("no tracking"). This is
            // why no separate field is needed — the two seams are the same object in production.
            _factory = new WeaponControllerFactory(_debugSettings, _ammoSource, _rng, _weaponStatSource,
                                                   _ammoResolver, _weaponStatSource as IManaSource);
        }

        /// <summary>
        /// Points <see cref="_weaponStatSource"/> at the owner's live <see cref="PFE.Systems.RPG.CharacterStats"/>,
        /// rebuilding the factory when the reference actually changed. Returns whether it changed.
        ///
        /// <para><b>Two callers, and the second one is why this exists.</b> <see cref="Equip"/> resolves
        /// it because AS3's <c>Weapon.setPers</c> runs at equip time. But the bare hooves are built in
        /// <see cref="Awake"/> — before any equip, and before <c>Construct</c> has even run — so the
        /// punch controller used to be born with a null source and keep it forever. Everything the
        /// hooves read through <c>IWeaponStatSource</c> then fell back to its "no owner" value:
        /// <c>PunchDamMult</c> 1 (so a kick's ×2 and ×1.5 never applied) and <c>KickDestroy</c> 0 (so
        /// the kick kept the row's <c>destroy='4'</c> instead of <c>Pers.kickDestroy</c> 30).</para>
        ///
        /// <para>Deliberately silent about the punch controller: it is the <i>caller's</i> job to
        /// rebuild it, because this method is itself called from <see cref="BuildPunchWeapon"/> and
        /// rebuilding there would recurse.</para>
        /// </summary>
        private bool ResolveOwnerStatSource()
        {
            if (_ownerUnit == null)
                _ownerUnit = GetComponentInParent<PFE.Entities.Units.UnitController>();

            var charStats = _ownerUnit != null
                ? _ownerUnit.GetComponent<PFE.Systems.RPG.CharacterStats>()
                : null;

            if (ReferenceEquals(charStats, _weaponStatSource))
                return false;

            _weaponStatSource = charStats;
            RebuildFactory();
            return true;
        }

        /// <summary>
        /// Find the swing's sweep surface — the <see cref="MeleeHitVolume"/> child of the weapon object.
        ///
        /// <para><b><c>includeInactive: true</c> is load-bearing, and its absence was a coin flip.</b>
        /// <c>MeleeHitVolume.Awake</c> disables its own GameObject (that is how the volume is off
        /// between swings), and <c>GetComponentInChildren&lt;T&gt;()</c> <b>skips inactive
        /// GameObjects</b>. Unity runs <c>Awake</c> in an unspecified order across GameObjects, so
        /// whether this component's own <c>Awake</c> found the volume depended on whether the child's
        /// <c>Awake</c> had already run. Losing that race left <c>_meleeHitVolume</c> null for the
        /// whole run, and a null volume is not "no sound": <c>HitVolume</c> on both controllers is
        /// null too, so the club and the hooves would deal <b>no unit damage and break no tiles</b>,
        /// with the shot's own sample still playing — a silent, total failure of melee.
        /// </para>
        ///
        /// <para>The volume is also the object <see cref="Construct"/> injects at <c>Start</c>, so a
        /// null here would additionally leave it without a <c>DamageSystem</c>, a sound service or an
        /// impact table.</para>
        /// </summary>
        private MeleeHitVolume ResolveMeleeHitVolume()
        {
            // Also covers this object itself, which is where an editor-authored rig sometimes puts it.
            return GetComponentInChildren<MeleeHitVolume>(includeInactive: true);
        }

        [Inject]
        public void Construct(IProjectileFactory projectileFactory, IObjectResolver resolver,
                              PfeDebugSettings debugSettings, ISoundService soundService,
                              PFE.Core.Rng.IRngService rng = null, PFE.Data.ContentRegistry registry = null)
        {
            _projectileFactory = projectileFactory;
            _resolver          = resolver;
            _soundService      = soundService;
            _debugSettings     = debugSettings;
            _rng               = rng;
            // Turn the ammo id a weapon names into its ballistics row. Built from the registry the
            // container owns rather than a separate registration, so there is exactly one place the
            // lookup is configured. A null registry (a test rig) leaves the resolver null, and every
            // shot then fires with the weapon's own numbers — AS3's "неправильный патрон" branch.
            _ammoResolver = registry != null ? new ContentRegistryAmmoResolver(registry) : null;
            // Unconditional: injection always supplies at least as much as Awake had, and the old
            // `??=` here meant an Awake-created factory was never upgraded with the real RNG.
            RebuildFactory();
        }

        // ── Runtime ──────────────────────────────────────────────────────────

        private WeaponControllerFactory _factory;
        private IWeaponController _current;

        /// <summary>
        /// AS3 <c>UnitPlayer.throwWeapon</c> (<c>:189</c>) — the weapon the grenade key fires.
        ///
        /// <para><b>Why this is a second controller and not "equip the grenade".</b> The whole point of
        /// <c>keyGrenad</c> is that it does <i>not</i> disturb the weapon in hand:
        /// <c>UnitPlayer.as:2188-2204</c> calls <c>throwWeapon.attack()</c> while
        /// <c>currentWeapon</c> keeps ticking. Before this field existed the only way to throw was to
        /// equip a <c>tip == 4</c> weapon, which replaced the gun — so the key had nothing to fire and
        /// the whole <c>ThrownObject</c>/<c>MineObject</c> stack was unreachable from the player.</para>
        ///
        /// <para><b>Filled from two places, exactly as in the oracle.</b> On
        /// <see cref="Equip"/>, a thrown weapon with no explicit assignment becomes the throw weapon
        /// (<c>:3693-3696</c>: <c>if(currentWeapon.tip == 4 &amp;&amp; this.invent.fav[29] == null)
        /// this.throwWeapon = currentWeapon;</c>). The explicit assignment — the player putting a
        /// grenade in favourite slot 29 — is <see cref="EquipThrow"/>.</para>
        /// </summary>
        private IWeaponController _throwController;

        /// <summary>Set by <see cref="ThrowGrenade"/>, consumed by the next <c>FixedUpdate</c>.</summary>
        private bool _throwRequested;

        // ── The bare hooves — AS3 `punchWeapon` ───────────────────────────────────────────────

        /// <summary>
        /// The player's bare-hoof strike weapon — AS3 <c>punchWeapon = new WKick(this,"punch")</c>
        /// (<c>UnitPlayer.as:431</c>), kept beside the weapon in hand
        /// (<c>childObjs = new Array(currentWeapon, this.punchWeapon)</c>, <c>:433</c>).
        ///
        /// <para><b>It is NOT built by the factory's dispatch.</b> The <c>punch</c> row is
        /// <c>tip='0'</c> with no <c>punch</c> attribute (<c>AllData.as:25</c>), so <c>Weapon.create()</c>
        /// would classify it as a base <i>ranged</i> weapon; AS3 never asks — it constructs
        /// <c>WKick</c> directly. See <see cref="WeaponControllerFactory.CreateHoofStrike"/>.</para>
        /// </summary>
        private UnarmedWeaponController _punchController;

        /// <summary>Set by <see cref="Punch"/>, consumed by the next <c>FixedUpdate</c>.</summary>
        private bool _punchRequested;

        /// <summary>The run key as it was when the punch was requested — AS3 reads <c>keyRun</c> at
        /// the press (<c>UnitPlayer.as:2379</c>), not per frame.</summary>
        private bool _punchKick;

        /// <summary>Where the punch weapon comes from when the Inspector leaves it empty.</summary>
        private const string DefaultPunchResourcePath = "Weapons/punch";

        /// <summary>
        /// The character's animation driver, for the two strike clips (<c>punch</c>/<c>kick</c>).
        /// Auto-resolved in <see cref="Awake"/>; null is legal (a bare test object) and then the strike
        /// simply has no animation — which is what the port shipped with: the clips and the two action
        /// states existed, and nothing ever called <c>TriggerAction</c>.
        /// </summary>
        private PFE.Character.Animation.CharacterAnimationDriver _animationDriver;

        /// <summary>
        /// The weapon the grenade key will fire, or null when the player has none. Exposed so the HUD
        /// and the dev console can say <i>which</i> throwable is armed rather than only whether one is.
        /// </summary>
        public IWeaponController ThrowWeapon => _throwController;

        private bool _wasReloading;     // tracks previous-frame reload state for sound trigger
        private bool _prepWasAttacking; // tracks previous-frame attack state for prep sound edges
        private readonly object _prepSoundKey = new object(); // stable key for ISoundService loop

        /// <summary>
        /// The unit that owns this loadout. Cached because the hold point is derived from its body
        /// every frame (<see cref="WeaponHoldPointMath"/>) and the muzzle push needs it too, while
        /// <c>GetComponentInParent</c> is a hierarchy walk that has no business in a fixed step.
        /// </summary>
        private PFE.Entities.Units.UnitController _ownerUnit;

        /// <summary>
        /// The player's spell caster, resolved lazily and cached.
        ///
        /// <para>Lazy rather than cached in <c>Awake</c> because <see cref="PFE.Entities.Player.PlayerController"/>
        /// adds the caster during its own <c>Awake</c> and Unity does not order two components' <c>Awake</c>
        /// calls — a cache taken too early would hold a null forever. <c>GetComponentInParent</c> is a
        /// hierarchy walk, so it is done once, on the first spell selection, and only re-done while it is
        /// still null.</para>
        /// </summary>
        private PlayerSpellCaster _spellCaster;

        // Pending shots from this FixedUpdate — forwarded to spawner/presenter.
        private readonly List<ShotPlan> _pendingPlans = new();

        // Same, for the grenade slot. A separate buffer so a throw in the same tick as a rifle shot
        // cannot be lost by the Clear() above.
        private readonly List<ShotPlan> _throwPlans = new();

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Currently active controller. Null if no weapon is equipped.</summary>
        public IWeaponController Current => _current;

        /// <summary>Set the world-space aim target. Called by PlayerController each Update.</summary>
        public void SetAimTarget(Vector2 worldTarget) => _aimTarget = worldTarget;

        /// <summary>
        /// The current world-space aim target, in <b>Unity units</b>. Read by the spell caster, which
        /// needs the same point the weapon is aiming at: AS3 casts at <c>World.w.celX</c>/<c>celY</c>
        /// (<c>UnitPlayer.as:2234</c>), the cursor, and this is the port's equivalent.
        /// </summary>
        public Vector2 AimTarget => _aimTarget;

        /// <summary>Equip a weapon by definition, disposing the previous controller.</summary>
        public void Equip(WeaponDefinition def)
        {
            if (def == null)
            {
                Debug.LogWarning("[PlayerWeaponLoadout] Equip called with null definition — skipping.");
                return;
            }

            // ── A spell is not equipped; it is cast ──────────────────────────────────────────────
            //
            // This is the seam AS3 uses, and the reason the `spell` flag exists at all: the nine
            // supportive-magic items are tip='5' like the assault magic, but they are items you use
            // from the inventory. UnitPlayer.changeWeapon sees the flag and calls
            // `invent.useItem(id)` instead of switching (UnitPlayer.as:3627-3635), so the weapon in
            // hand is left untouched.
            //
            // Returning here — before the Dispose below — reproduces that: selecting a spell does
            // not disarm you.
            if (def.spell)
            {
                // The dispatch the earlier passes left as a refusal. `useItem`'s tip == "spell" branch
                // (Invent.as:625-633) calls `gg.changeSpell(id)`, and changeSpell only *toggles the
                // selection* — it never casts. The cast happens on the next tick, on the Def key or a
                // favourite hotkey (UnitPlayer.as:2226-2284), which PlayerSpellCaster now drives.
                //
                // LearnSpell first, then Select: `Select` is a lookup, so an id the book has never seen
                // would be treated as "unknown id" and clear the selection instead (the oracle relies on
                // that for the deselect case, Invent.as:849-852). The item being used is what proves the
                // spell is owned, which is exactly AS3's precondition — `invent.spells[id]` is populated
                // by addSpell when the item is taken, before useItem can be reached.
                var caster = ResolveSpellCaster();
                if (caster == null)
                {
                    Debug.LogWarning(
                        $"[PlayerWeaponLoadout] '{def.weaponId}' is a supportive spell (AS3 weapon@spell) " +
                        "but no PlayerSpellCaster was found on the player — the spell cannot be selected. " +
                        "Nothing is equipped, which is correct: a spell is never a weapon.");
                    return;
                }

                caster.LearnSpell(def.weaponId);
                Spell selected = caster.SelectSpell(def.weaponId);

                Debug.Log(selected != null
                    ? $"[PlayerWeaponLoadout] '{def.weaponId}' selected as the current spell " +
                      "(AS3 changeSpell) — cast it with the Def key."
                    : $"[PlayerWeaponLoadout] '{def.weaponId}' toggled OFF (AS3 changeSpell is a toggle) — " +
                      "no spell is selected.");
                return;
            }

            _soundService?.StopLoop(_prepSoundKey);
            _prepWasAttacking = false;
            _current?.Dispose();

            // Stamp the owner's faction onto the weapon state — this is the only place that knows
            // both the weapon and whose it is, and FactionRule needs the attacker's side at hit time.
            // AS3 reads the same value as `this.weap.owner.fraction`. Resolved through the parent
            // because the loadout sits on the player or one of its children; a missing UnitController
            // leaves Neutral, which makes the weapon hit everyone (AS3's own default) rather than
            // silently picking a side.
            // Awake caches the owner; re-resolve only if it was missing then, for a rig assembled
            // after Awake. The common path therefore does no hierarchy walk.
            if (_ownerUnit == null)
                _ownerUnit = GetComponentInParent<PFE.Entities.Units.UnitController>();

            FactionType ownerFaction = FactionType.Neutral;
            var ownerUnit = _ownerUnit;
            if (ownerUnit != null) ownerFaction = ownerUnit.Faction;

            // Resolve the owner's RPG stats before building the controller — AS3 reads them in
            // Weapon.setPers, which runs at equip time. Done here rather than in Awake because the
            // UnitController / CharacterStats may be added to the hierarchy after this component
            // wakes, and because the reference only needs refreshing when it actually changes: the
            // source is read live on every shot, so a perk taken mid-fight needs no re-equip.
            //
            // The bare hooves read the same object (`Pers.kickDestroy`, `Pers.punchDamMult`), so a
            // source that appears here has to reach the punch controller too — it is built in Awake,
            // before any equip, and would otherwise keep the null source it was born with. That is
            // exactly the defect that made a kick carry the row's `destroy='4'` instead of
            // `kickDestroy` 30: a septum barricade (hp 30) could never be opened by a kick.
            bool statsChanged = ResolveOwnerStatSource();
            var charStats = _weaponStatSource as PFE.Systems.RPG.CharacterStats;

            if (statsChanged && _punchController != null)
                BuildPunchWeapon();

            _current = _factory.Create(def, ownerFaction);

            if (_current == null)
            {
                // The factory returns null for a definition with no held controller. Today that is
                // only a supportive spell, which this method refuses above — but the next lines
                // dereference _current, so the possibility is guarded rather than assumed. A silent
                // NRE here would surface as an unrelated crash in the presenter.
                Debug.LogWarning($"[PlayerWeaponLoadout] No controller for '{def.weaponId}' — nothing equipped.");
                return;
            }

            // Wire MeleeHitVolume to the controller when it's a melee weapon.
            if (_current is MeleeWeaponController meleeCtrl)
            {
                meleeCtrl.HitVolume = _meleeHitVolume;
                _meleeHitVolume?.SetActive(false);   // ensure disabled until first swing
            }
            else
            {
                // Ensure hit volume is off when any non-melee weapon is equipped.
                _meleeHitVolume?.SetActive(false);
            }

            // Notify sub-systems about the new weapon.
            _weaponPresenter?.SetState(_current.State, def);
            _projectileSpawner?.SetWeapon(def);

            // Sync weapon skill tier to owner's UnitStats (charStats resolved above).
            if (ownerUnit != null && ownerUnit.UnitStats != null && charStats != null)
            {
                ownerUnit.UnitStats.weaponSkillLevel = charStats.GetSkillTierForWeapon(def.skillLevel);
            }

            // ── Record the equipped weapon on the inventory ────────────────────────────────────────
            //
            // The port's counterpart to AS3's `gg.newWeapon`: PipPageInv reads its id for the selection
            // frame (`ramka`, :391) and its `ammoBase` for the ammo page's "the loaded round sorts first"
            // nudge (:286). Both were dead because `GameInventory.CurrentWeaponId` — the field that means
            // exactly this — was read in three places (the PipBuck source, the save snapshot, and
            // ToggleWeaponRespect's unequip) and written by none. It was only ever restored from a save,
            // so a weapon equipped in play was never the current one: no selection frame, and the ammo
            // page sorted a loaded round as if the weapon were empty.
            //
            // Written here, after `_current` is known non-null, so the guarded "no controller for this
            // definition" branch above cannot leave the inventory claiming a weapon that is not in hand.
            if (_ammoSource is PlayerInventory ownerInventory && ownerInventory.Inventory != null)
            {
                ownerInventory.Inventory.CurrentWeaponId = def.weaponId;
            }

            // ── The grenade slot fills itself ─────────────────────────────────────────────────────
            //
            // AS3 `UnitPlayer.as:3693-3696`, in the tail of `changeWeaponNow`:
            //   if(currentWeapon.tip == 4 && this.invent.fav[29] == null) this.throwWeapon = currentWeapon;
            // i.e. switching TO a throwable makes it the grenade weapon, but only while the player has
            // not assigned slot 29 explicitly — an explicit choice is never overwritten by a switch.
            //
            // Recorded divergence: AS3 shares one object between the two roles, so the two share
            // `t_attack`/`t_auto`. This builds a second controller instead. The visible difference is
            // that throwing and firing a grenade-as-active-weapon keep separate cooldowns; the shared
            // form would need `EndAttack` on the throw to be conditional on the attack key still being
            // held, and getting that wrong silently kills a held trigger.
            if (def.weaponType == WeaponType.Thrown && _throwController == null)
            {
                EquipThrow(def);
            }

            Debug.Log($"[PlayerWeaponLoadout] Equipped '{def.weaponId}'.");
        }

        /// <summary>
        /// Fill the grenade slot — AS3 <c>throwWeapon</c>, either from the player's favourite slot 29
        /// (<c>UnitPlayer.as:436</c>) or automatically when a thrown weapon is switched to
        /// (<c>:3693-3696</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Player-owned, so <c>getAmmo()</c> takes the inventory branch.</b>
        /// <c>WThrow.getAmmo()</c> (<c>:268-280</c>) splits on <c>owner.player</c>: the player consumes
        /// a real item, an NPC spends its own four-round counter. <c>_factory</c> was built without
        /// <c>npcOwner</c>, so <see cref="ThrownWeaponController"/> resolves the player half — which is
        /// why a player with no <c>bomb</c> in the bag gets a silent no-throw rather than infinite
        /// grenades.</para>
        /// </remarks>
        public void EquipThrow(WeaponDefinition def)
        {
            if (def == null) return;

            if (def.weaponType != WeaponType.Thrown)
            {
                Debug.LogWarning(
                    $"[PlayerWeaponLoadout] '{def.weaponId}' is weaponType {def.weaponType}, not Thrown — " +
                    "the grenade slot only takes tip == 4 weapons (AS3 WThrow).");
                return;
            }

            _throwController?.Dispose();

            if (_ownerUnit == null)
                _ownerUnit = GetComponentInParent<PFE.Entities.Units.UnitController>();

            FactionType ownerFaction = _ownerUnit != null ? _ownerUnit.Faction : FactionType.Neutral;

            _throwController = _factory?.Create(def, ownerFaction);

            Debug.Log(_throwController != null
                ? $"[PlayerWeaponLoadout] Grenade slot armed with '{def.weaponId}' (AS3 throwWeapon)."
                : $"[PlayerWeaponLoadout] No controller for throwable '{def.weaponId}' — grenade slot empty.");
        }

        /// <summary>
        /// Request one throw. Returns false when there is nothing in the slot, which is the
        /// <c>else { this.ctr.keyGrenad = false; }</c> arm of <c>UnitPlayer.as:2197-2200</c>.
        /// </summary>
        /// <remarks>
        /// Deferred to the next <c>FixedUpdate</c> rather than fired here, because a shot is only
        /// produced by <c>Tick</c>: <c>ThrownWeaponController.RunAttack</c> arms <c>t_attack</c> and
        /// <c>RunActions</c> is what reaches <c>Shoot</c>, both inside the same tick. Firing on the
        /// input edge would need the controller to be ticked from the input callback, which runs at
        /// render rate and would make the fuse and the arc depend on the framerate.
        /// </remarks>
        public bool ThrowGrenade()
        {
            if (_throwController == null) return false;

            _throwRequested = true;
            return true;
        }

        // ── The bare hooves ───────────────────────────────────────────────────

        /// <summary>
        /// Build the punch weapon — AS3 <c>UnitPlayer</c>'s constructor, where <c>punchWeapon</c> is
        /// created <b>directly</b> as a <c>WKick</c> (<c>:431</c>) rather than through
        /// <c>Weapon.create()</c>.
        ///
        /// <para><b>Why that distinction matters.</b> The <c>punch</c> row is
        /// <c>&lt;weapon id='punch' tip='0' cat='0' skill='1' tipdec='5'&gt;</c> with no
        /// <c>punch</c> attribute (<c>AllData.as:25</c>). <c>Weapon.create()</c> tests <c>tip</c> first
        /// and <c>punch</c> second, so the factory would hand this row to the <b>ranged</b> controller —
        /// correct for the dispatch, wrong for this weapon. AS3 sidesteps its own factory here, and so
        /// does the port.</para>
        /// </summary>
        private void BuildPunchWeapon()
        {
            // The owner's RPG stats must be in hand *before* the controller is constructed: the
            // constructor takes the source by reference and every accessor reads it live, but a
            // source that is null at construction time is null for the object's whole life.
            ResolveOwnerStatSource();

            if (_factory == null) RebuildFactory();

            WeaponDefinition def = _punchWeaponDef;
            if (def == null)
                def = Resources.Load<WeaponDefinition>(DefaultPunchResourcePath);

            if (def == null)
            {
                Debug.LogWarning(
                    "[PlayerWeaponLoadout] No punch weapon definition (Resources/" +
                    DefaultPunchResourcePath + ") — F will do nothing. AS3 always has one: " +
                    "UnitPlayer.as:431 constructs it unconditionally.", this);
                return;
            }

            if (_ownerUnit == null)
                _ownerUnit = GetComponentInParent<PFE.Entities.Units.UnitController>();

            FactionType ownerFaction = _ownerUnit != null ? _ownerUnit.Faction : FactionType.Neutral;

            // Rebuilds happen when the stat source is resolved after Awake (see Equip), so the old
            // controller's reactive subscriptions are released rather than leaked.
            _punchController?.Dispose();

            _punchController = _factory?.CreateHoofStrike(def, ownerFaction);
            if (_punchController == null)
            {
                Debug.LogWarning("[PlayerWeaponLoadout] Could not build the punch weapon.", this);
                return;
            }

            _punchController.HitVolume = _meleeHitVolume;
            _punchController.ProbeTile = ProbeTileForHoofStrike;
        }

        /// <summary>
        /// The player's <c>F</c> key — AS3 <c>keyPunch</c>, <c>UnitPlayer.as:2377-2385</c>.
        ///
        /// <para><b><paramref name="runHeld"/> is the whole difference between the two strikes.</b>
        /// <c>(this.punchWeapon as WKick).kick = this.ctr.keyRun;</c> (<c>:2379</c>): hold run and the
        /// same button throws the back hooves — ×2 damage, ×1.5 knock, and <c>Pers.kickDestroy</c>'s
        /// 30 points of tile damage instead of the row's 4. It is not the cursor, and it is not "am I
        /// facing away from the mouse": <c>WKick</c> never reads <c>celX</c>/<c>celY</c>.</para>
        ///
        /// <para>Deferred to the next <c>FixedUpdate</c> for the same reason the grenade is: a strike is
        /// only produced by <c>Tick</c>, and firing from the input callback would make its cadence
        /// depend on the render rate.</para>
        /// </summary>
        public bool Punch(bool runHeld)
        {
            if (_punchController == null) return false;

            _punchRequested = true;
            _punchKick      = runHeld;

            // AS3 switches the label when the 13-frame `t_work` starts (UnitPlayer.as:4271-4283), i.e.
            // on the press, not on the impact frame.
            TriggerStrikeAnimation(runHeld);
            return true;
        }

        /// <summary>
        /// Advance the punch weapon by one fixed step — AS3 runs <c>punchWeapon.actions()</c> every
        /// frame regardless of what is in hand (<c>UnitPlayer.as:1202-1205</c>), which is why this is
        /// ticked even when <c>_current</c> is null.
        /// </summary>
        private void TickPunch()
        {
            // The owner's stats are added by PlayerController's own Awake, which may run after this
            // component's — so the source can be missing when the hooves are first built. Re-resolve
            // once it appears and rebuild, otherwise the kick keeps `kickDestroy` 0 and the row's
            // `destroy='4'` for the rest of the run (a septum barricade is 30 hp: unbreakable).
            // Guarded on the null source, so the steady state costs one reference test per tick.
            if (_weaponStatSource == null && ResolveOwnerStatSource())
                BuildPunchWeapon();

            if (_punchController == null) return;

            Vector2 holdPoint = ResolveHoldPoint();
            Vector2 hornPoint = ResolveHornPoint();

            // Same per-tick push the melee controller gets: the controller is a plain class with no
            // unit, and every line it binds is measured from the FEET (WKick.as:60-101).
            _punchController.Owner = ResolveHoofOwnerFrame();

            bool fire = _punchRequested;
            _punchRequested = false;

            if (fire)
            {
                _punchController.Kick = _punchKick;
                _punchController.BeginAttack();
            }

            _punchController.Tick(Time.fixedDeltaTime, holdPoint, hornPoint, _aimTarget);

            // One press, one strike — AS3 clears `keyPunch` inside the block (:2384).
            if (fire) _punchController.EndAttack();

            string strikeSound = _punchController.ConsumeStrikeSound();
            if (!string.IsNullOrEmpty(strikeSound))
                _soundService?.Play(strikeSound, holdPoint);

            foreach (ShotPlan plan in _punchController.FlushShotPlans())
                _pendingPlans.Add(plan);
        }

        /// <summary>
        /// The body the hooves strike from. <c>owner.Y</c> is the <b>feet</b> here, not the body centre
        /// the melee spring wants — see <see cref="HoofOwnerFrame"/>.
        /// </summary>
        private HoofOwnerFrame ResolveHoofOwnerFrame()
        {
            if (_ownerUnit == null)
                return new HoofOwnerFrame(transform.position.x, transform.position.y, 1f);

            return new HoofOwnerFrame(
                _ownerUnit.transform.position.x,
                _ownerUnit.FeetWorldY,
                _ownerUnit.FacingDirection);
        }

        /// <summary>
        /// AS3's <c>loc.getAbsTile(X + storona*60, Y - 30|50)</c> (<c>WKick.as:60-61</c>), as a physics
        /// probe.
        ///
        /// <para><b>Air is a tile with <c>thre</c> 0, not a missing tile.</b> <c>getAbsTile</c> returns
        /// the cell's <c>Tile</c> object whatever is in it (<c>Location.as:2333-2340</c>), and
        /// <c>Tile.thre</c> defaults to 0 (<c>Tile.as:24</c>) — so "no collider here" must answer
        /// <c>(exists: true, threshold: 0)</c>, which is exactly what makes the probe fire for the case
        /// it exists for: a wall at 30 px and air at 50 px. Answering "no tile" would invert the flag
        /// on every wall in the game.</para>
        ///
        /// <para>The 2 px box is the same idiom <c>MeleeHitVolume.DestructibleColliderAtPixel</c> uses,
        /// and for the same reason: <c>Physics2D</c> has no cheap non-deprecated point query, and 2 px
        /// is well inside a 40 px cell.</para>
        /// </summary>
        private static HoofTileProbe ProbeTileForHoofStrike(Vector2 world)
        {
            Collider2D[] hits = Physics2D.OverlapBoxAll(world, new Vector2(0.02f, 0.02f), 0f);

            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D hit = hits[i];
                if (hit == null || hit.isTrigger) continue;

                var tile = hit.GetComponent<PFE.Systems.Map.Rendering.TileCollider>();
                if (tile == null) continue;

                PFE.Systems.Map.TileData data = tile.GetTileData();
                return new HoofTileProbe(true, data != null ? data.damageThreshold : 0f);
            }

            return new HoofTileProbe(true, 0f);
        }

        /// <summary>
        /// Play the <c>punch</c> or <c>kick</c> clip. Both exist in the shipped animation data
        /// (<c>SWFData.StateLabels</c>: <c>punch</c> 3664, <c>kick</c> 3665) and both action states were
        /// already written — nothing had ever called <c>TriggerAction</c>, so the player had no strike
        /// animation at all.
        /// </summary>
        private void TriggerStrikeAnimation(bool kick)
        {
            _animationDriver?.TriggerAction(kick
                ? PFE.Character.Animation.AnimationAction.Kick
                : PFE.Character.Animation.AnimationAction.Punch);
        }

        /// <summary>
        /// The player's <see cref="PlayerSpellCaster"/>, or null when the rig has none.
        ///
        /// <para>Resolved through the parent because the loadout may sit on a child of the player, and
        /// the caster is added to the player root. A null result is reported by the caller rather than
        /// swallowed, because "the spell did not select" and "there is no caster" have different fixes
        /// and look identical from the outside.</para>
        /// </summary>
        private PlayerSpellCaster ResolveSpellCaster()
        {
            if (_spellCaster == null)
                _spellCaster = GetComponentInParent<PlayerSpellCaster>();
            return _spellCaster;
        }

        // ── Input API (called by PlayerController) ────────────────────────────

        public void BeginAttack()
        {
            if (_debugSettings?.LogWeaponControllerDiagnostics == true)
                Debug.Log($"[PlayerWeaponLoadout] BeginAttack() forwarding to '{_current?.GetType().Name ?? "null"}'.");
            _current?.BeginAttack();
        }

        public void EndAttack()
        {
            if (_debugSettings?.LogWeaponControllerDiagnostics == true)
                Debug.Log($"[PlayerWeaponLoadout] EndAttack() forwarding to '{_current?.GetType().Name ?? "null"}'.");
            _current?.EndAttack();
        }

        public void StartReload()
        {
            if (_debugSettings?.LogWeaponControllerDiagnostics == true)
                Debug.Log($"[PlayerWeaponLoadout] StartReload() forwarding to '{_current?.GetType().Name ?? "null"}'.");
            _current?.StartReload();
        }

        // ── Reactive state accessors for HUD ──────────────────────────────────
        // R3 uses ReactiveProperty<T> directly (no IReadOnlyReactiveProperty interface like UniRx).

        public ReactiveProperty<int>   CurrentAmmo       => _current?.State.CurrentAmmoRP;
        public ReactiveProperty<bool>  IsReloading       => _current?.State.IsReloadingRP;
        public ReactiveProperty<float> ReloadProgress    => _current?.State.ReloadProgressRP;
        public ReactiveProperty<int>   CurrentDurability => _current?.State.CurrentDurabilityRP;

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            // Route the Awake fallback through RebuildFactory like every other site. The old inline
            // `??=` spelling had already drifted once (it dropped the RNG) and would have dropped the
            // ammo resolver too — a factory built here is only ever replaced by Construct, so an
            // inline copy can go stale in exactly the window a weapon is equipped from the Inspector.
            if (_factory == null)
                RebuildFactory();

            _ownerUnit = GetComponentInParent<PFE.Entities.Units.UnitController>();

            if (_mounts == null)
                _mounts = GetComponent<WeaponMounts>() ?? GetComponentInChildren<WeaponMounts>();

            if (_weaponPresenter == null)
                _weaponPresenter = GetComponentInChildren<WeaponPresenter>();

            if (_projectileSpawner == null)
                _projectileSpawner = GetComponentInChildren<ProjectileSpawner>();

            if (_meleeHitVolume == null)
                _meleeHitVolume = ResolveMeleeHitVolume();

            // The character rig, for the magic horn point. Optional: an enemy rig or a bare test
            // object has none, and then ResolveHornPoint takes AS3's `catch` branch.
            if (_characterAssembler == null)
                _characterAssembler = GetComponentInChildren<PFE.Character.CharacterSpriteAssembler>();

            if (_animationDriver == null)
                _animationDriver = GetComponentInParent<PFE.Character.Animation.CharacterAnimationDriver>();

            BuildPunchWeapon();

            if (_mounts == null)
                Debug.LogWarning("[PlayerWeaponLoadout] No WeaponMounts found — mount positions will use transform.position. " +
                                 "Add WeaponMounts to the player and assign hold point Transforms.", this);
        }

        private void Start()
        {
            // Initialize the spawner with the factory resolved via DI.
            // Done in Start so [Inject] Construct() has already run.
            if (_projectileSpawner != null && _projectileFactory != null)
            {
                _projectileSpawner.Initialize(_projectileFactory, _resolver, _debugSettings);

                // AS3's `owner`, which the mine placement retry falls back to when the hand position
                // is inside a wall (`WThrow.as:165 setPos(owner.X, owner.Y)`). The unit's transform
                // IS its feet — a unit's collider bottom is its origin — which is exactly the point
                // the oracle picks, so no offset is applied. `_ownerUnit` is resolved in Awake.
                _projectileSpawner.Owner = _ownerUnit != null ? _ownerUnit.transform : transform;

                // …and the gun itself, for a beam's post-impact re-anchor (`Bullet.as:233-237`). Same
                // presenter the per-tick `MuzzleWorldPoint` above comes from, read live rather than
                // copied, so a round fired while walking keeps its tail on a barrel that is still
                // moving. A loadout with no presenter leaves it null — a beam then keeps the tail it
                // was fired with, which is AS3's `if(this.weap)` guard.
                _projectileSpawner.SetMuzzleSource(
                    () => _weaponPresenter != null ? _weaponPresenter.MuzzleWorldPosition : null);

                if (_debugSettings?.LogProjectileSpawning == true)
                    Debug.Log("[PlayerWeaponLoadout] ProjectileSpawner initialized with IProjectileFactory.");
            }
            else if (_projectileSpawner != null)
                Debug.LogWarning("[PlayerWeaponLoadout] IProjectileFactory not injected — ProjectileSpawner will not fire.", this);

            // MeleeHitVolume uses [Inject] for IPublisher<DamageDealtMessage> but is a
            // scene MonoBehaviour, not registered in the container. Inject it manually
            // via the resolver so MessagePipe publisher is wired without adding it to the
            // container separately.
            if (_meleeHitVolume != null && _resolver != null)
                _resolver.Inject(_meleeHitVolume);

            if (_startingWeaponDef != null)
                Equip(_startingWeaponDef);
            else
                Debug.LogWarning("[PlayerWeaponLoadout] No starting weapon assigned. Player will start unarmed.", this);
        }

        private void Update()
        {
            // Push current aim target to the presenter each frame so LateUpdate has fresh data.
            _weaponPresenter?.SetAimTarget(_aimTarget);
        }

        private void FixedUpdate()
        {
            // The grenade slot ticks even when the player is unarmed — AS3 runs
            // `throwWeapon.actions()` from `UnitPlayer.as:1202-1205` unconditionally, which is what
            // advances `t_attack`/`t_auto` between throws. Placed before the `_current == null` early
            // return so a weaponless player can still lob a grenade.
            TickThrowWeapon();

            // The hooves are a SECOND weapon slot (AS3 `childObjs`), so they tick whether or not
            // something is in hand — and a punch with no weapon equipped is the normal case.
            // `_pendingPlans` is cleared here rather than below so both paths can share the
            // plan-consumption block.
            _pendingPlans.Clear();
            TickPunch();

            if (_current == null)
            {
                ConsumePendingPlans(ResolveHoldPoint());
                return;
            }

            Vector2 holdPoint = ResolveHoldPoint();
            Vector2 hornPoint = ResolveHornPoint();

            // Hand the ranged controller the barrel tip the presenter drew last frame, so the round
            // leaves the muzzle instead of the weapon's origin. Must happen BEFORE Tick(), because
            // Tick() is what runs Shoot() and consumes the value.
            //
            // The value is one LateUpdate old, which is the correct pairing rather than a lag: the
            // presenter computes it from State.X/Y *after* the controller's own Tick wrote them, so
            // the point matches the frame the gun is currently drawn at. AS3 reads vis.emit at
            // fire time from the same display list it just animated.
            if (_current is RangedWeaponController ranged)
                ranged.MuzzleWorldPoint = _weaponPresenter != null ? _weaponPresenter.MuzzleWorldPosition : null;

            // The melee spring's body frame — AS3 reads `owner.X`, `owner.Y - scY/2` and
            // `owner.weaponKrep` straight out of `WClub.actions()`. Same per-tick push as the muzzle
            // point above, and for the same reason: the controller is a plain class with no unit.
            if (_current is MeleeWeaponController melee)
                melee.Owner = ResolveMeleeOwnerFrame();

            if (_debugSettings?.LogWeaponControllerDiagnostics == true)
            {
                var state = _current.State;
                if (state.TAttack > 0 || state.TReload > 0 || state.TPrep > 0)
                {
                    Debug.Log(
                        $"[PlayerWeaponLoadout] FixedUpdate pre-Tick weapon='{state.Def.weaponId}' " +
                        $"ammo={state.CurrentAmmo} tAttack={state.TAttack} tReload={state.TReload} tPrep={state.TPrep} " +
                        $"hold={holdPoint} aim={_aimTarget}.");
                }
            }

            _current.Tick(Time.fixedDeltaTime, holdPoint, hornPoint, _aimTarget);

            // ── Prep sound — minigun/flamer spin-up loop (AS3: sndPrep / t1 / t2) ──
            TickPrepSound();

            // ── Reload sound — fires once on the frame reload begins ──────────
            bool nowReloading = _current.State.IsReloadingRP.Value;
            if (_soundService != null && nowReloading && !_wasReloading)
            {
                var rSnd = _current.State.Def.soundReload;
                if (!string.IsNullOrEmpty(rSnd))
                    _soundService.Play(rSnd, holdPoint);
            }
            _wasReloading = nowReloading;

            // Collect ShotPlans for this tick. The hooves' plans are already in the buffer.
            _pendingPlans.AddRange(_current.FlushShotPlans());

            if (_debugSettings?.LogWeaponControllerDiagnostics == true)
            {
                var state = _current.State;
                if (_pendingPlans.Count > 0 || state.TAttack > 0 || state.TReload > 0 || state.TPrep > 0)
                {
                    Debug.Log(
                        $"[PlayerWeaponLoadout] FixedUpdate post-Tick weapon='{state.Def.weaponId}' " +
                        $"ammo={state.CurrentAmmo} tAttack={state.TAttack} tReload={state.TReload} tPrep={state.TPrep} " +
                        $"plans={_pendingPlans.Count}.");
                }
            }

            ConsumePendingPlans(holdPoint);
        }

        /// <summary>
        /// Hand this tick's <see cref="ShotPlan"/>s to the volume, the sound service and the spawner.
        ///
        /// <para><b>Split out of <c>FixedUpdate</c> so the unarmed path can reach it.</b> The hooves are
        /// ticked before the "is anything in hand?" guard — a punch with no weapon equipped is the
        /// normal case — and that path has no <c>_current</c> to read a sound id from, so the id comes
        /// from the plan's own cue instead.</para>
        /// </summary>
        private void ConsumePendingPlans(Vector2 holdPoint)
        {
            if (_pendingPlans.Count == 0) return;

            // Forward damage context of the latest MeleeSweep plan to MeleeHitVolume.
            // Multiple plans per tick are rare for melee, but we take the last one.
            if (_meleeHitVolume != null)
            {
                for (int i = _pendingPlans.Count - 1; i >= 0; i--)
                {
                    if (_pendingPlans[i].Kind == ShotKind.MeleeSweep)
                    {
                        _meleeHitVolume.SetDamageContext(_pendingPlans[i].Damage);
                        break;
                    }
                }
            }

            // ── Fire sound — first plan with PlayShootSound flag ──────────
            if (_soundService != null && _current != null)
            {
                for (int i = 0; i < _pendingPlans.Count; i++)
                {
                    if (_pendingPlans[i].Cues.PlayShootSound)
                    {
                        var sSnd = _current.State.Def.soundShoot;
                        if (!string.IsNullOrEmpty(sSnd))
                            _soundService.Play(sSnd, holdPoint);
                        break;
                    }
                }
            }

            _projectileSpawner?.SpawnFromPlans(_pendingPlans);
        }

        /// <summary>
        /// Whether an authored mount may be trusted as a world-space point for this rig.
        ///
        /// <para><b>The arrangement this rejects, and why it is not hypothetical.</b> The presenter
        /// writes the weapon's world position <i>from</i> the hold point. A hold point that is a
        /// descendant of the weapon therefore moves when the weapon moves, and the weapon chases it:
        /// measured from <c>Player.prefab</c>, the <c>muzzle</c> node sits 0.33 u out and 0.04 u up from
        /// the weapon node, and <c>RangedWeaponController</c> closes 1/5 of the gap every flash frame
        /// (<c>State.X += (holdPoint.x - State.X) / 5</c>) — so the gun walks away from the character at
        /// roughly 2 u/s. All three mounts in that prefab are wired to that node, which is the reported
        /// "levitation".</para>
        ///
        /// <para>A null <paramref name="mount"/> means "nothing authored" and is not usable either.
        /// A null presenter means nothing is being positioned, so there is no loop to create.</para>
        /// </summary>
        private bool MountIsUsable(Transform mount)
            => mount != null && (_weaponPresenter == null || !mount.IsChildOf(_weaponPresenter.transform));

        /// <summary>
        /// Advance the grenade slot by one fixed step and spawn anything it threw.
        ///
        /// <para><b>One tick per throw, not a held trigger.</b> <c>ThrownWeaponController.RunAttack</c>
        /// sets <c>IsAttack</c> and arms <c>t_attack</c>; <c>RunActions</c> then reaches <c>Shoot</c> when
        /// <c>t_attack == rapid</c>, which is the same tick. So the request is bracketed by
        /// <c>BeginAttack</c>/<c>EndAttack</c> around a single <c>Tick</c> — exactly AS3's
        /// <c>throwWeapon.attack()</c>, a single call from the key handler
        /// (<c>UnitPlayer.as:2194</c>), not a held trigger like the rifle's.</para>
        ///
        /// <para><b>Aim and hold point are shared with the held weapon on purpose.</b> AS3 fires from
        /// the same unit at the same <c>celX</c>/<c>celY</c> cursor and uses
        /// <c>UnitPlayer.setWeaponPos</c> for the throw origin too; the throw's own arc is then computed
        /// by <c>WThrow.getVel</c> from that origin to the cursor.</para>
        /// </summary>
        private void TickThrowWeapon()
        {
            if (_throwController == null) return;

            Vector2 holdPoint = ResolveHoldPoint();
            Vector2 hornPoint = ResolveHornPoint();

            bool fire = _throwRequested;
            _throwRequested = false;

            if (fire) _throwController.BeginAttack();

            _throwController.Tick(Time.fixedDeltaTime, holdPoint, hornPoint, _aimTarget);

            if (fire) _throwController.EndAttack();

            _throwPlans.Clear();
            _throwPlans.AddRange(_throwController.FlushShotPlans());

            if (_throwPlans.Count == 0) return;

            // Sound and the shot itself, mirroring the held weapon's tail. The throw's own
            // `sndShoot` is the weapon's (`<snd shoot='mine_s'/>` on `bomb`), not a hard-coded cue.
            if (_soundService != null)
            {
                for (int i = 0; i < _throwPlans.Count; i++)
                {
                    if (!_throwPlans[i].Cues.PlayShootSound) continue;

                    var throwSnd = _throwController.State.Def.soundShoot;
                    if (!string.IsNullOrEmpty(throwSnd))
                        _soundService.Play(throwSnd, holdPoint);
                    break;
                }
            }

            _projectileSpawner?.SpawnFromPlans(_throwPlans);
        }

        /// <summary>
        /// Where the held weapon sits this tick — the port of AS3 <c>UnitPlayer.setWeaponPos</c>.
        ///
        /// <para><b>Why the derived point wins.</b> AS3 has no hold-point marker: the point is computed
        /// every frame from the owner's body box and the cursor, and it is the only value that cannot
        /// depend on the thing it positions. The loadout used to read
        /// <c>WeaponMounts.WeaponHoldPoint</c> unconditionally, and in the shipped rig that resolves to a
        /// node inside the weapon itself — see <see cref="MountIsUsable"/> for the measured drift that
        /// produced. A derived point makes that wiring inert, which is the correct outcome for a
        /// mis-wired mount.</para>
        ///
        /// <para><b>An authored mount is still honoured where nothing can be derived.</b> With no
        /// <c>UnitController</c> there is no body box, so a rig that supplies its own point (and that
        /// point is not inside the weapon) keeps it.</para>
        /// </summary>
        private Vector2 ResolveHoldPoint()
        {
            if (_ownerUnit == null)
            {
                // No UnitController: an enemy rig or a bare test object. Nothing to derive a body box
                // from, so fall back rather than inventing one.
                Transform authored = _mounts != null ? _mounts.WeaponHoldPointTransform : null;
                return MountIsUsable(authored)
                    ? _mounts.WeaponHoldPoint
                    : (Vector2)transform.position;
            }

            UnitDefinition stats = _ownerUnit.Stats;
            IWeaponStats def = _current.State.Def;

            var inputs = new WeaponHoldPointMath.Inputs
            {
                OwnerX     = _ownerUnit.transform.position.x,
                // The unit origin is the feet — see UnitController.FeetWorldY for the three places
                // that agree on it. AS3's `Y` is the same point.
                OwnerFeetY = _ownerUnit.FeetWorldY,
                BodyWidth  = stats != null && stats.Width  > 0f ? stats.Width  : 0.5f,
                BodyHeight = stats != null && stats.Height > 0f ? stats.Height : 0.7f,
                FacingSign = _ownerUnit.FacingDirection,
                AimX       = _aimTarget.x,
                // WeaponType is imported straight from the AS3 tip attribute
                // (`WeaponDataImporter.cs:213` — `(WeaponType)AttrI(rootAttrs, "tip", 0)`), so the
                // enum's numeric value *is* the oracle's `tip`.
                Tip        = (int)def.weaponType,
                // AS3 `weaponKrep` is the owner's `krep` attribute, which the port stores as
                // UnitDefinition.isStable (`UnitDataImporter.cs:553-558`, `krep == 1`). It is the
                // branch selector: zero means "derive the hold point", non-zero means "use the base
                // class's flat waist height".
                WeaponKrep = stats != null && stats.isStable ? 1f : 0f,

                // Not wired: the port tracks no per-unit stair state, no weapon-up state and no
                // weapon-swap phase, so these stay at their "none of that is happening" values. Each
                // only ever moves the weapon back toward the body, so leaving them false is the
                // open-ground behaviour — the one the reported bug is about. Named here rather than
                // silently defaulted so the next reader can see exactly what is and is not ported.
                OnStairs       = false,
                Stay           = false,
                WeaponUp       = false,
                SwappingWeapon = false,
            };

            // No tile predicate: the loadout has no room reference, and the two clamps that use it
            // only pull the weapon back off a wall it would otherwise poke into. Passing null means
            // they never fire, which is AS3's result in open ground.
            return WeaponHoldPointMath.Resolve(inputs, isSolidAt: null);
        }

        /// <summary>
        /// The player's body, for <see cref="MeleeWeaponController.Owner"/>.
        ///
        /// <para><b><c>rigidMount</c> is hard <c>false</c> for the player, and that is not an
        /// approximation.</b> <c>UnitPlayer.as:368</c> assigns <c>weaponKrep = 0</c> in the
        /// constructor, so a player's melee weapon always takes <c>WClub.actions()</c>'s <i>spring</i>
        /// branch — it hovers at the aim point instead of being carried. Reading
        /// <c>UnitDefinition.weaponKrep</c> here (whose initialiser is <b>1</b>) would make the
        /// player's club snap rigidly to the hold point, which is the exact bug this frame exists to
        /// fix.</para>
        ///
        /// <para><c>clampStep</c> is <c>true</c>: the per-frame travel limit is gated on
        /// <c>owner.player</c> (<c>WClub.as:328</c>), and this is the player.</para>
        /// </summary>
        private MeleeWeaponController.OwnerFrame ResolveMeleeOwnerFrame()
        {
            if (_ownerUnit == null)
            {
                // No UnitController: an enemy rig or a bare test object. The rig origin stands in for
                // the body, which is what the hold point's own fallback does.
                return new MeleeWeaponController.OwnerFrame(
                    transform.position.x, transform.position.y, 1f, rigidMount: false, clampStep: true);
            }

            UnitDefinition stats = _ownerUnit.Stats;
            float height = stats != null && stats.Height > 0f ? stats.Height : 0.7f;

            return new MeleeWeaponController.OwnerFrame(
                bodyX:          _ownerUnit.transform.position.x,
                // AS3 `owner.Y - owner.scY/2` — the body's middle, which is what a thrust and an
                // overhead rotate about. The unit origin is the feet.
                bodyCentreY:    _ownerUnit.FeetWorldY - height * 0.5f,
                bodyFacingSign: _ownerUnit.FacingDirection,
                rigidMount:     false,
                clampStep:      true);
        }

        /// <summary>
        /// The magic / horn mount — <b>the <c>konec</c> bone of the head</b>, read live
        /// (<c>UnitPlayer.as:3563-3584</c>). AS3 takes <c>vis.osn.body.head.morda.konec</c> through
        /// <c>localToGlobal</c> every frame; the port asks the rig that draws the character for the
        /// same point, and only falls back to the body when there is no rig to ask.
        ///
        /// <para><b>Why the bone, and not the authored mount or a body height.</b> This is where magic
        /// actually leaves from: <c>Weapon.actions()</c> snaps a <c>tip==5</c> weapon to
        /// <c>owner.magicX/magicY</c> (<c>Weapon.as:1070-1074</c>) and a magic symbol has no
        /// <c>emit</c> child, so <c>getBulXY()</c> returns that snapped point. The port used
        /// <c>ResolveMagicFallback</c> — the oracle's <c>catch</c> branch — as if it were the answer,
        /// which put every spell at a fixed chest height instead of on the horn. The bone also
        /// <i>moves</i>: 687 distinct positions across the shipped clips, because it rides the head.
        /// </para>
        ///
        /// <para><b>Why the authored mount does not simply win.</b> The shipped rig points
        /// <c>_magicHoldPoint</c> at the same <c>muzzle</c> node inside the weapon, so reading it
        /// unguarded is the identical feedback loop the weapon hold point had — and worse for magic,
        /// because the magic controller <i>snaps</i> the weapon to the horn point rather than lerping
        /// toward it, so the loop has no damping at all. The mount is honoured only where the bone
        /// cannot be read and the mount is not inside the weapon.</para>
        /// </summary>
        private Vector2 ResolveHornPoint()
        {
            // The bone first — this is the oracle's `try` branch, and the only one that runs in play.
            if (_characterAssembler != null &&
                _characterAssembler.TryGetPartWorldPosition(MagicHornPartName, out Vector2 hornBone))
            {
                if (_ownerUnit == null)
                    return hornBone;

                UnitDefinition boneStats = _ownerUnit.Stats;

                // No tile predicate, for the same reason ResolveHoldPoint passes none: the loadout has
                // no room reference. A null predicate means AS3's wall guard never fires, which is its
                // own open-ground result. Wiring it is a one-argument change once the loadout can reach
                // the land.
                return WeaponHoldPointMath.ResolveMagicHorn(
                    hornBone,
                    ownerX:      _ownerUnit.transform.position.x,
                    ownerFeetY:  _ownerUnit.FeetWorldY,
                    bodyHeight:  boneStats != null && boneStats.Height > 0f ? boneStats.Height : 0.7f,
                    isSitting:   false,          // the port has no seated state
                    isSolidAt:   null);
            }

            if (_ownerUnit == null)
            {
                if (_mounts == null) return (Vector2)transform.position;

                // Keep the documented fallback chain — horn, then hold point, then the rig origin —
                // but skip any link that lives inside the weapon. A circular mount is worse than no
                // mount: it drags the weapon instead of holding it.
                if (MountIsUsable(_mounts.MagicHoldPointTransform))  return _mounts.MagicHoldPoint;
                if (MountIsUsable(_mounts.WeaponHoldPointTransform)) return _mounts.WeaponHoldPoint;
                return (Vector2)transform.position;
            }

            // No rig to ask — AS3's `catch` branch.
            UnitDefinition stats = _ownerUnit.Stats;
            return WeaponHoldPointMath.ResolveMagicFallback(
                _ownerUnit.transform.position.x,
                _ownerUnit.FeetWorldY,
                stats != null && stats.Height > 0f ? stats.Height : 0.7f);
        }

        /// <summary>
        /// AS3 <c>vis.osn.body.head.morda.konec</c> — the head part the magic point is read from.
        /// Named here rather than inline so the one string that ties the weapon system to the rig's
        /// part table is findable.
        /// </summary>
        public const string MagicHornPartName = "konec";

        /// <summary>
        ///
        /// minigun_s (and similar) is a single audio file with three sections:
        ///   [0 .. t1]   spin-up     (plays proportional to current prep charge on trigger press)
        ///   [t1 .. t2]  fire loop   (loops while weapon is actively firing)
        ///   [t2 .. end] spin-down   (triggered on trigger release while still spinning)
        ///
        /// AS3 uses millisecond positions (sndCh.position); soundPrepT1/T2 are stored in ms.
        /// Unity AudioSource.time is in seconds, so divide by 1000.
        /// </summary>
        private void TickPrepSound()
        {
            if (_soundService == null) return;
            var def = _current.State.Def;
            if (string.IsNullOrEmpty(def.soundPrep)) return;

            // State.WasAttack: whether attack was held during this tick's flash frames.
            // State.TPrep: current prep charge (>0 means weapon is spinning / wound up).
            bool isAttacking = _current.State.WasAttack;
            int  tPrep       = _current.State.TPrep;

            float t1Sec = def.soundPrepT1 / 1000f;
            float t2Sec = def.soundPrepT2 / 1000f;

            if (!_prepWasAttacking && isAttacking)
            {
                // Rising edge — start sound, seeking past the spin-up section proportional
                // to how wound-up the weapon already is. AS3: Snd.ps(sndPrep, x, y, t_prep*30).
                float startSec = tPrep * (1f / 30f); // 1 flash frame = 1/30 s → ms = frame*33, but AS3 uses *30
                _soundService.PlayLoopFromTime(def.soundPrep, _prepSoundKey, startSec);
            }
            else if (isAttacking && t1Sec > 0f && t2Sec > 0f)
            {
                // Loop-back: while firing, when playback approaches t2 jump back to t1.
                // AS3: if sndCh.position > snd_t_prep2 - 300 → restart at snd_t_prep1 + 200.
                float pos = _soundService.GetLoopTime(_prepSoundKey);
                if (pos >= 0f && pos > t2Sec - 0.3f)
                    _soundService.PlayLoopFromTime(def.soundPrep, _prepSoundKey, t1Sec + 0.2f);
            }
            else if (_prepWasAttacking && !isAttacking && tPrep > 0)
            {
                // Falling edge while still spinning — jump to spin-down section.
                // AS3: if sndCh.position < snd_t_prep2 - 400 → restart at snd_t_prep2 + 100.
                float pos = _soundService.GetLoopTime(_prepSoundKey);
                if (t2Sec > 0f && pos >= 0f && pos < t2Sec - 0.4f)
                    _soundService.PlayLoopFromTime(def.soundPrep, _prepSoundKey, t2Sec + 0.1f);
                // If already past t2 (in spin-down), let it play naturally to clip end.
            }
            else if (!isAttacking && tPrep == 0)
            {
                // Weapon fully wound down — stop.
                _soundService.StopLoop(_prepSoundKey);
            }

            _prepWasAttacking = isAttacking;
        }

        private void OnDestroy()
        {
            _soundService?.StopLoop(_prepSoundKey);
            _current?.Dispose();
            _current = null;
            _punchController?.Dispose();
            _punchController = null;
            _weaponPresenter?.ClearState();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_mounts == null)
                _mounts = GetComponent<WeaponMounts>() ?? GetComponentInChildren<WeaponMounts>();
            if (_weaponPresenter == null)
                _weaponPresenter = GetComponentInChildren<WeaponPresenter>();
            if (_projectileSpawner == null)
                _projectileSpawner = GetComponentInChildren<ProjectileSpawner>();
            if (_meleeHitVolume == null)
                _meleeHitVolume = ResolveMeleeHitVolume();
        }
#endif
    }
}
