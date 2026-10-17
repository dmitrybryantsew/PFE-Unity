using System.Collections.Generic;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// The ArmedShooter family's controller (AS3 <c>fe/unit/UnitRaider.as</c> and its subclasses
    /// <c>UnitSlaver</c>, <c>UnitZebra</c>, <c>UnitRanger</c>, <c>UnitEncl</c>, <c>UnitMerc</c>,
    /// <c>UnitNecros</c>).
    ///
    /// <para><b>What this class owns: the weapon the unit is actually holding.</b> AS3 rolls it in the
    /// constructor — <c>currentWeapon = getXmlWeapon(param2)</c> (<c>UnitRaider.as:179</c>), where
    /// <c>param2</c> is the location's <c>locDifLevel</c> — and then derives <c>attackerType</c> from
    /// what it rolled (<c>:257-272</c>). The brain reads the result and picks its attack branch; this
    /// class performs the roll and holds the answer, which is the same split the alicorn uses (controller
    /// equips, brain fires).</para>
    ///
    /// <para><b>The divergence this replaces.</b> The brain used to classify itself from the unit id
    /// (<c>raider1..4</c> melee, <c>raider5..8</c> ranged, <c>raider9</c> thrown) and from substrings of
    /// the first candidate weapon id (<c>contains "club"</c> → melee, <c>contains "grenade"</c> →
    /// thrown). Both are inventions: the oracle's answer is a function of the weapon it <i>rolled</i> —
    /// a per-spawn random draw over the whole candidate list, gated by difficulty — and of the unit's own
    /// <c>krep</c>. Under the old code a <c>raider1</c> that rolled <c>mach</c> (tip 0, a gun) still
    /// fought as a contact attacker, and no enemy ever held a weapon object at all.</para>
    ///
    /// <para><b>What this class does not own: the secondary weapon.</b> Rows carrying AS3's <c>f</c>
    /// attribute (<c>mercgr</c> on <c>merc1..5</c>, <c>robomlau</c>/<c>robogas</c> on <c>ranger1..3</c>,
    /// <c>mercgr</c> on <c>encl</c>) are <b>excluded from the roll</b> by
    /// <see cref="UnitWeaponSelector.SelectIndex"/> — which is the faithful half, and the half whose
    /// absence silently armed every mercenary with a rocket launcher. Actually <i>firing</i> them is a
    /// separate mechanism in the oracle: the family constructor builds the weapon
    /// (<c>UnitMerc.as:32</c>, <c>UnitEncl.as:29</c>, <c>UnitRanger.as:28-29</c>) and
    /// <c>attack()</c> fires it on its own <c>t_gren</c> countdown, entirely outside
    /// <c>attackerType</c>. That is the next step for this family and is <b>not</b> wired here — these
    /// units currently roll and fire their primary weapon only. Note also that
    /// <c>merc1</c>/<c>merc2</c>/<c>merc4</c> author no <c>&lt;un grenader&gt;</c>, so their <c>mercgr</c>
    /// row is inert even in the oracle (<c>UnitMerc.as:30</c> guards on <c>grenader &gt; 0</c>).</para>
    /// </summary>
    [RequireComponent(typeof(ArmedShooterBrain))]
    public class ArmedShooterController : EnemyController
    {
        public const string ControllerId = "UnitRaider";
        public const string ControllerAlias = "raider";

        /// <summary>
        /// The placement-<c>ai</c> reader this family uses — AS3 <c>UnitRaider.as:227-241</c>, inherited
        /// by <c>UnitEncl</c>, <c>UnitMerc</c>, <c>UnitRanger</c>, <c>UnitZebra</c> and
        /// <c>UnitSlaver</c> because they all extend <c>UnitRaider</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Set here and not on <c>UnitController</c>, because the mapping is a class fact.</b>
        /// This family reads <c>stay</c>, <c>quiet</c> and <c>sniper</c>; a robot reads only
        /// <c>quiet</c>, an alicorn reads <c>stay</c>/<c>quiet</c>/<c>pole</c>, and a hellhound reads
        /// none of them. See <see cref="UnitPlacementAi"/> for the table and
        /// <c>UnitController.PlacementStaysPut</c> for what the answer drives.</para>
        ///
        /// <para><b>This is also the only family whose unit row can clear <c>stroll</c>.</b>
        /// <c>UnitRaider.as:351-354</c> reads <c>node0.un.@stay</c> and no other class reads that
        /// attribute, which is why the row half of <c>PlacementStaysPut</c> is gated on this family.</para>
        /// </remarks>
        protected override UnitAiFamily AiFamily => UnitAiFamily.Raider;

        /// <summary>
        /// Reusable candidate buffer, so a roll allocates nothing. Filled from
        /// <see cref="UnitDefinition.weapons"/> and handed to <see cref="UnitWeaponSelector"/>.
        /// </summary>
        private readonly List<WeaponOption> _weaponOptions = new List<WeaponOption>(8);

        /// <summary>Whether the roll has run, so a second <c>Initialize</c> cannot re-roll the weapon.</summary>
        private bool _weaponRolled;

        /// <summary>
        /// AS3 <c>UnitRaider.attackerType</c> (<c>:83</c>, assigned at <c>:257-272</c>) — how this unit
        /// attacks: 0 contact, 1 melee-weapon swing, 2 ranged, 3 thrown. The brain branches on it; see
        /// <see cref="UnitWeaponSelector.AttackerType"/> for the ladder.
        /// </summary>
        public int AttackerType { get; private set; }

        /// <summary>
        /// The weapon id <see cref="UnitWeaponSelector.SelectIndex"/> accepted, or <c>null</c> when no
        /// candidate was eligible (AS3 <c>getXmlWeapon</c> returning <c>null</c>, which the oracle treats
        /// as "no weapon" and answers with <c>attackerType = 0</c>).
        /// </summary>
        public string EquippedWeaponId { get; private set; }

        /// <summary>
        /// AS3 <c>weaponKrep</c> — the unit's <c>&lt;comb krep&gt;</c>, whose declared default is
        /// <b>1</b> (<c>Unit.as:332</c>) and which is only overwritten when the attribute is present
        /// (<c>Unit.as:1166-1169</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Read from <c>UnitDefinition.isStable</c>, which is the importer's name for exactly
        /// this value</b> (<c>krep == 1</c>). One divergence is recorded rather than hidden: the importer
        /// writes <c>isStable</c> only when <c>&lt;comb krep&gt;</c> exists, so "attribute absent" and
        /// "<c>krep='0'</c>" both read <c>false</c> where the oracle would answer 1 for the first. Of the
        /// 139 <c>&lt;comb&gt;</c> nodes in <c>AllData.as</c>, 45 carry <c>krep</c> and 94 do not — and
        /// <b>every ArmedShooter unit that holds a <c>tip &lt;= 1</c> weapon carries it explicitly</b>
        /// (the only unit without one, <c>necros</c>, has an empty weapon list and therefore lands on
        /// <c>attackerType</c> 0 either way). So this family is exact today. The affected units are 17
        /// Robot / Flyer / Boss rows — <c>dron1..3</c>, <c>owl</c>, the bloat/slime family,
        /// <c>phoenix</c> and four bosses — and they are the follow-up: a dedicated <c>weaponKrep</c>
        /// field plus a re-import, which belongs with those families' passes, not this one.</para>
        /// </remarks>
        public bool WeaponKrep { get; private set; }

        /// <summary>
        /// The live mount for the rolled weapon, or <c>null</c> when nothing was rolled or the mount could
        /// not be built (no definition, or a bare test spawn with no weapon services). The brain fires
        /// through this.
        /// </summary>
        public EnemyWeaponMount WeaponMount =>
            EquippedWeaponId != null ? GetWeaponMount(EquippedWeaponId) : null;

        /// <summary>
        /// Throws left on the rolled weapon's NPC counter, or <c>-1</c> when it is not a thrown weapon or
        /// no mount was built. AS3 reclassifies a thrower whose counter has run out as a contact attacker
        /// (<c>UnitRaider.as:1546-1549</c>); the brain reads this to do the same.
        /// </summary>
        public int RemainingThrows => WeaponMount != null ? WeaponMount.RemainingThrows : -1;

        protected override void Awake()
        {
            base.Awake();
            if (_brain == null)
            {
                _brain = GetComponent<ArmedShooterBrain>();
            }
        }

        /// <summary>
        /// Roll the weapon, then let the base run its normal sequence.
        /// </summary>
        /// <remarks>
        /// <para><b>The roll has to precede <c>base.Initialize</c>, and that is not stylistic.</b>
        /// <c>EnemyController.Initialize</c> calls <c>_brain.OnDefinitionAssigned()</c>, which is where
        /// <c>ArmedShooterBrain.ApplyDefinitionTuning</c> reads <see cref="AttackerType"/>. Rolling
        /// afterwards would leave the brain classified from the property's default for the whole life of
        /// the unit — the exact "value written but never read at the right time" shape this port keeps
        /// finding.</para>
        ///
        /// <para>The roll itself only needs the definition and <see cref="EnemyController.LocationDifficulty"/>,
        /// both of which are already set. Equipping needs <c>Stats</c> and the weapon services, so it runs
        /// after the base call — and again from <c>OnWeaponServicesReady</c> if the services arrive later.</para>
        /// </remarks>
        public override void Initialize(UnitDefinition definition, UnitStats stats)
        {
            if (!_weaponRolled)
            {
                _weaponRolled = true;
                RollWeapon(definition);
            }

            base.Initialize(definition, stats);

            EquipRolledWeapon();
        }

        /// <summary>
        /// A late weapon-services handover reaches an archetype that already ran <c>Initialize</c>, so the
        /// rolled weapon is equipped here. Idempotent: <c>EquipWeapon</c> is per-id, and a unit whose roll
        /// produced nothing returns immediately.
        /// </summary>
        protected override void OnWeaponServicesReady() => EquipRolledWeapon();

        /// <summary>
        /// AS3 <c>UnitRaider</c>'s constructor weapon choice (<c>UnitRaider.as:162-181</c>), then the
        /// <c>attackerType</c> ladder (<c>UnitRaider.as:257-272</c>).
        ///
        /// <para><b>Three arms, and the roll is only the last of them.</b> The oracle's chain is
        /// <c>param4.weap</c> (a runtime override object, no port equivalent) → the placement's
        /// <c>param3.@weap</c> → <c>getXmlWeapon(param2)</c>, the unit row's <c>&lt;w&gt;</c> roll. The
        /// middle arm is what this method now reads; see <see cref="UnitPlacementWeapon"/> for why
        /// <c>weap=""</c> is "explicitly unarmed" rather than "roll anyway".</para>
        ///
        /// <para><b>Why this can run twice.</b> <c>RoomUnitSpawner</c> calls <c>Initialize</c> (which
        /// rolls) before <c>ApplyPlacement</c> (which supplies the placement), so the first roll cannot
        /// see the attribute. The <c>ApplyPlacement</c> override re-rolls when the attribute turns out
        /// to matter, and <see cref="EnemyController.ClearWeaponMounts"/> drops the first weapon. Both
        /// happen before the first <c>SimTick</c>.</para>
        /// </summary>
        private void RollWeapon(UnitDefinition definition)
        {
            WeaponDefinition rolled = null;
            EquippedWeaponId = null;

            // ── Arm 2: the placement's `weap` (UnitRaider.as:170-176) ────────────────────────────────
            //
            // Gated on the raider family because that is the only family AS3 lets read it — `UnitNecros`
            // extends `Unit`, not `UnitRaider`, and this controller is shared with it for port
            // convenience.
            //
            // The gate is applied to the MODE, not wrapped around the switch below. Folding it into the
            // switch would leave a necros placement authoring `weap=""` looking like `Unarmed` to the
            // roll guard further down, so it would be disarmed by an attribute its AS3 class never
            // reads. No shipped necros placement authors `weap` today — the nineteen that do are
            // raider/ranger/merc/slaver/zebra — which is precisely why the bug would have been invisible
            // until someone authored one.
            PlacementWeaponMode mode = AiFamily == UnitAiFamily.Raider
                ? PlacementWeaponMode
                : PlacementWeaponMode.Roll;

            switch (mode)
            {
                case PlacementWeaponMode.Fixed:
                    // `Weapon.create(this, param3.@weap)` — which strips a trailing `^n` BEFORE the
                    // id lookup (Weapon.as:352-356). Without the strip, the two shipped placements
                    // that author `lmg^1` and `zebr^1` would resolve to nothing and fight unarmed.
                    EquippedWeaponId = UnitPlacementWeapon.BaseWeaponId(PlacementWeapText);
                    TryGetWeaponDefinition(EquippedWeaponId, out rolled);
                    break;

                case PlacementWeaponMode.Unarmed:
                    // `weap=""` — the inner `!= ""` fails, `currentWeapon` stays null, and the roll
                    // below is never reached.
                    EquippedWeaponId = null;
                    rolled = null;
                    break;
            }

            // ── Arm 3: the unit row's roll (Unit.as:1450-1475) ───────────────────────────────────────
            //
            // Reached only when the placement authored no `weap` at all. Note this is the oracle's
            // `else`, so a `Fixed`/`Unarmed` answer above must NOT fall through — which is why the
            // switch sets `EquippedWeaponId` and the guard below tests it rather than re-testing the
            // mode.
            if (EquippedWeaponId == null && mode != PlacementWeaponMode.Unarmed)
            {
                WeaponChance[] candidates = definition != null ? definition.weapons : null;
                if (candidates != null && candidates.Length > 0)
                {
                    _weaponOptions.Clear();
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        _weaponOptions.Add(new WeaponOption(
                            candidates[i].weaponId,
                            candidates[i].chance,
                            candidates[i].difficulty,
                            // AS3 `f` — the row is this unit's SECONDARY weapon (mercgr for merc/encl,
                            // robomlau/robogas for ranger, …). The oracle's roll steps over it
                            // (`Unit.as:1459`) and the family constructor grants it by name instead, so it
                            // must not be offered here. See WeaponRowParser for the full account.
                            candidates[i].isFixedWeapon));
                    }

                    // `getXmlWeapon(param1:int)` truncates the caller's `locDifLevel` (a Number) — the
                    // parameter is typed int at the declaration, so AS3 does the same.
                    int difficulty = (int)LocationDifficulty;

                    int index = UnitWeaponSelector.SelectIndex(
                        _weaponOptions,
                        difficulty,
                        // AS3's `isrnd` is `Math.random() < p` — Flash's *unseeded* global, not the room's
                        // seeded spawn stream. UnityEngine.Random is the port's equivalent global, and using
                        // it here deliberately leaves the seeded room stream untouched: a spawn that consumed
                        // a number from it would desynchronise every later roll in the room, which is the
                        // same reason AlicornController does not roll `osob` at spawn.
                        roll01: () => UnityEngine.Random.value,
                        // `Weapon.create` returns null for an id that is not in the weapon table, and the
                        // oracle then CONTINUES to the next candidate rather than giving up (Unit.as:1470).
                        isAvailable: id => TryGetWeaponDefinition(id, out _));

                    if (index != UnitWeaponSelector.NoWeapon)
                    {
                        EquippedWeaponId = _weaponOptions[index].WeaponId;
                        TryGetWeaponDefinition(EquippedWeaponId, out rolled);
                    }
                }
            }

            // AS3 Unit.as:332 — `weaponKrep` defaults to 1; see WeaponKrep's remarks for how the port
            // recovers it.
            WeaponKrep = definition != null && definition.isStable;

            AttackerType = UnitWeaponSelector.AttackerType(
                rolled != null ? (int)rolled.weaponType : 0,
                WeaponKrep,
                rolled != null);
        }

        /// <summary>
        /// Build the mount for the rolled weapon, once both the definition (<c>Stats</c>) and the weapon
        /// services are present. Both guards are load-bearing, exactly as they are for the alicorn:
        /// <c>RoomUnitSpawner</c> hands the services over <i>before</i> <c>Initialize</c> and
        /// <c>Faction</c> is derived from <c>Stats</c>, so equipping on the early hook alone would build a
        /// mount that does not know whose side it is on.
        /// </summary>
        private void EquipRolledWeapon()
        {
            if (string.IsNullOrEmpty(EquippedWeaponId)) return;
            if (Stats == null || !HasWeaponServices) return;

            EquipWeapon(EquippedWeaponId);
        }

        /// <summary>
        /// Re-roll the weapon now that the placement is known, when the placement's <c>weap</c>
        /// attribute decides the outcome.
        ///
        /// <para><b>Why an override at all.</b> AS3 reads <c>param3.@weap</c> inside the unit's
        /// constructor, so the placement is known before any weapon is chosen
        /// (<c>UnitRaider.as:162-181</c>). The port's spawner calls <c>Initialize</c> — which rolls — at
        /// <c>RoomUnitSpawner.cs:1149</c> and <c>ApplyPlacement</c> at <c>:1233</c>, so the first roll
        /// cannot see the attribute. Re-rolling here is the same fix shape as
        /// <c>UnitNPCController.SyncAnimatorFlight</c>: the fact arrives late, so the dependent state is
        /// recomputed at the arrival seam rather than polled every frame.</para>
        ///
        /// <para><b>Only when the placement actually decides.</b> A unit whose placement authors no
        /// <c>weap</c> keeps the weapon its first roll produced — re-rolling would consume a second
        /// <c>Math.random()</c> for no reason and change the weapon of every un-authored raider in the
        /// game. A unit whose placement says <c>weap=""</c> or names a weapon is re-rolled, and the first
        /// weapon's mount is destroyed so it is not also held.</para>
        /// </summary>
        public override void ApplyPlacement(UnitInstance placement)
        {
            base.ApplyPlacement(placement);

            // The same family gate as `RollWeapon`, and it has to be here too: a necros placement
            // authoring `weap` must not trigger a re-roll, because AS3's necros never reads it.
            if (AiFamily != UnitAiFamily.Raider) return;
            if (!_weaponRolled) return;

            PlacementWeaponMode mode = PlacementWeaponMode;
            if (mode == PlacementWeaponMode.Roll) return;

            // The definition the first roll used is gone by now; re-read it from the same source
            // `Initialize` was given. `Stats` is not it — this needs the unit row's <w> candidates,
            // which `RollWeapon` only consults on the `Roll` arm, so a null here costs nothing.
            ClearWeaponMounts();
            RollWeapon(Definition);
            EquipRolledWeapon();
        }
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class RaiderController : ArmedShooterController
    {
        public new const string ControllerId = "UnitRaider";
        public new const string ControllerAlias = "raider";
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class SlaverController : ArmedShooterController
    {
        public new const string ControllerId = "UnitSlaver";
        public new const string ControllerAlias = "slaver";
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class ZebraController : ArmedShooterController
    {
        public new const string ControllerId = "UnitZebra";
        public new const string ControllerAlias = "zebra";
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class RangerController : ArmedShooterController
    {
        public new const string ControllerId = "UnitRanger";
        public new const string ControllerAlias = "ranger";
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class EnclController : ArmedShooterController
    {
        public new const string ControllerId = "UnitEncl";
        public new const string ControllerAlias = "encl";
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class MercController : ArmedShooterController
    {
        public new const string ControllerId = "UnitMerc";
        public new const string ControllerAlias = "merc";
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class NecrosController : ArmedShooterController
    {
        public new const string ControllerId = "UnitNecros";
        public new const string ControllerAlias = "necros";

        /// <summary>
        /// <c>UnitNecros</c> reads <c>stay</c> and nothing else — it extends <c>Unit</c>, <b>not</b>
        /// <c>UnitRaider</c> (<c>UnitNecros.as:102</c>: <c>if(aiTip != "stay" &amp;&amp; isrnd())</c>), and
        /// has no <c>stroll</c> or <c>quiet</c> field behind it.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the override is mandatory rather than cosmetic.</b> This controller derives from
        /// <see cref="ArmedShooterController"/> because it shares the weapon-rolling controller — a port
        /// convenience, not an oracle fact. Without this override it would inherit
        /// <see cref="UnitAiFamily.Raider"/> and answer <c>quiet</c>/<c>sniper</c> as well, so a
        /// hypothetical <c>ai="quiet"</c> necros would stand still where the oracle lets it wander. The
        /// shipped data authors <c>stay</c> on seven necros placements and <c>quiet</c> on none, so the
        /// divergence is latent rather than visible — which is exactly the kind that survives a
        /// play-test.</para>
        /// </remarks>
        protected override UnitAiFamily AiFamily => UnitAiFamily.Necros;
    }
}
