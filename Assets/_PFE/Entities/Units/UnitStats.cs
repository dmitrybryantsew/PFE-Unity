using System;
using System.Collections.Generic;
using R3;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Combat;
using PFE.Systems.Effects;

namespace PFE.Entities.Units
{
/// <summary>
/// Reactive stats system for units.
/// Acts as the data container for both RPG stats and Combat calculations.
///
/// <para><b>Also the owner of this unit's live status effects.</b> AS3 keeps
/// <c>effects:Array</c> on the base <c>Unit</c> class (<c>Unit.as:494</c>) and every unit — player and
/// every NPC — steps it from <c>Unit.step</c> and adds to it from <c>Unit.addEffect</c>. The port's
/// equivalent seam is here: <see cref="UnitStats"/> is the mutable per-unit state object that both an
/// NPC's <c>UnitController</c> and the player's stack hold, so the effect set lives on it rather than
/// on any one controller. See <see cref="Effects"/> and <see cref="TickEffects"/>.</para>
/// </summary>
public class UnitStats : IEffectHost
{
// === Core Vitals (Reactive) ===
public readonly ReactiveProperty<float> CurrentHp;
public readonly ReactiveProperty<float> MaxHp;
public readonly ReactiveProperty<float> Mana;
public readonly ReactiveProperty<float> MaxMana;

// === Combat Offense Stats ===
    // Critical Hit modifiers
    public float critChanceBonus = 0f;
    public float critChanceBonusAdditional = 0f;
    public float critDamageBonus = 0f; // Added to multiplier (e.g. +0.5 to make 2.5x)

    /// <summary>
    /// The <b>stealth-crit</b> probability carried by this unit's shots — AS3 <c>Unit.critInvis</c>
    /// (<c>Unit.as:322</c>), granted by the sneak skill (<c>AllData.as:5280</c>) and copied onto the
    /// bullet at <c>Weapon.as:1697</c>. <b>A property of the attacker</b>, which is why it lives here
    /// on the shooter's stats and is read into <see cref="PFE.Systems.Weapons.DamageContext"/> at fire
    /// time rather than consulted at hit time.
    ///
    /// <para>Read by <see cref="PFE.Systems.Combat.DamageCalculator.ResolveDamage"/> as a second,
    /// independent crit roll (fixing AS3's <c>Unit.damage():3659-3666</c>): when the target is not the
    /// shooter's current look-at and is not a non-living unit, a pass doubles the damage again.</para>
    /// </summary>
    public float critInvisChance = 0f;

    /// <summary>
    /// The <b>disintegration</b> probability carried by this unit's shots — AS3 <c>Pers.desintegr</c>
    /// (<c>Pers.as:199</c>), granted by the level-15 <c>desintegr</c> perk (<c>AllData.as:5497-5500</c>)
    /// and copied onto the bullet via <c>Weapon.setPers</c> (<c>Weapon.as:967-970</c>) and
    /// <c>shoot()</c> (<c>:1525-1527</c>). Like <see cref="critInvisChance"/>, a property of the
    /// attacker, read at hit time only as a gate.
    ///
    /// <para>Consumed by <c>DamageCalculator.ResolveDamage</c> for AS3's
    /// <c>Unit.damage():3671-3677</c>: on a laser or plasma hit against a target whose current HP is at
    /// most ten times the incoming damage, a pass multiplies the damage by 12 — an overkill finisher,
    /// not a general damage buff.</para>
    /// </summary>
    public float desintegrChance = 0f;

    /// <summary>
    /// Whether this unit is one of AS3's <c>doop</c> units — the non-living classes that set
    /// <c>doop = true</c> in their constructors (<c>UnitDamager</c>, <c>UnitDestr</c>, <c>UnitMWall</c>,
    /// <c>UnitNecros</c>, <c>UnitPhoenix</c>, <c>UnitScythe</c>, <c>UnitSlime</c>, <c>UnitSpectre</c>,
    /// <c>UnitTrain</c>, <c>UnitTransmitter</c>, <c>UnitTrap</c>, <c>UnitTrigger</c>, <c>Mine</c>,
    /// <c>VirtualUnit</c>, and the player).
    ///
    /// <para>Read by <see cref="PFE.Systems.Combat.DamageCalculator.ResolveDamage"/> to suppress the
    /// stealth crit (<c>Unit.damage():3659</c>, <c>!this.doop</c>). <b>Defaults to <c>false</c></b>, the
    /// direction that <i>grants</i> the stealth crit — correct for the ~90% of units that are living
    /// (raiders, monsters, robots and turrets all take stealth crits; robots do <b>not</b> set
    /// <c>doop</c>).</para>
    ///
    /// <para><b>Not yet wired to data.</b> AS3 sets <c>doop</c> from the unit's <c>@cl</c> class, which
    /// <c>UnitDataImporter</c> does not import — so every unit currently answers <c>false</c>. That is a
    /// recorded divergence, not an omission: the only units affected are the 14 AllData rows whose
    /// <c>@cl</c> is one of the classes above, and until they are imported they will take stealth crits
    /// they should not. See the class doc of <see cref="UnitStats"/> and the crit track notes.</para>
    /// </summary>
    public bool isNonLiving = false;

    // General Damage modifiers
    public float damageBonus = 0f;      // Flat damage add
    public float damageMultiplier = 1f; // Multiplicative bonus

    // Weapon Context (Synced from equipped weapon)
    public int weaponSkillLevel = 1;
    public int weaponCurrentDurability = 100;

    // === Combat Defense Stats ===
    private ArmourState _armour = ArmourState.None;

    /// <summary>
    /// The equipped armour's projection. <see cref="ArmourState.None"/> when nothing is equipped.
    ///
    /// <para>This replaces the bare <c>armor</c> float that used to live here. That field was never
    /// assigned anywhere in production, so nothing read a non-zero value; the armour item model
    /// (<c>GameArmorInstance</c>) existed but had no path into combat. This is that path.</para>
    ///
    /// <para><b>Read-only on purpose.</b> It was a public field, and a public field on a projection
    /// that also has a reactive mirror (<see cref="ArmourIntegrity"/>) is a desync waiting to happen:
    /// any external <c>stats.armour.integrity = …</c> would change the combat value without moving the
    /// bar. Every write now goes through <see cref="EquipArmour(IArmourItem)"/>,
    /// <see cref="UnequipArmour"/>, <see cref="ApplyDamage"/> or <see cref="RepairArmour"/>, which all
    /// end in <c>SetArmour</c> and therefore all publish. Callers that used to assign the field should
    /// call <see cref="EquipArmour(in ArmourState)"/> instead.</para>
    /// </summary>
    public ArmourState armour => _armour;

    /// <summary>
    /// The item behind <see cref="armour"/>, when the projection came from one. Null for a unit pool
    /// and for a bare projection. Held so condition changes can be written back to the single source
    /// of truth — see <see cref="IArmourItem"/>.
    /// </summary>
    public IArmourItem ArmourItem { get; private set; }

    /// <summary>
    /// Armour integrity as a fraction 0..1, for the HUD bar. 0 when nothing is equipped.
    ///
    /// <para>A <c>ReactiveProperty</c> rather than a computed getter, deliberately: the HUD needs to
    /// be told when armour changes, and <see cref="armour"/> is a struct that R3 cannot observe. Note
    /// this is the same shape as <see cref="CurrentHp"/>/<see cref="MaxHp"/> and, unlike
    /// <see cref="HpPercent"/>, it does <b>not</b> allocate a subscription per read.</para>
    /// </summary>
    public readonly ReactiveProperty<float> ArmourIntegrity = new(0f);

    /// <summary>
    /// Whether any armour is projected. The HUD bar's visibility gate.
    ///
    /// <para>AS3 gates its bar on <c>armor_qual &gt; 0</c> (<c>Unit.as:3004</c>), which is the unit
    /// pool's field. Gating on "is equipped" is the item model's equivalent and is the honest one:
    /// <c>armor_qual</c> is a <i>probability</i>, so a plate with <c>qual = 0</c> would be invisible on
    /// the HUD while still absorbing wear and still breaking.</para>
    /// </summary>
    public readonly ReactiveProperty<bool> HasArmour = new(false);

    /// <summary>
    /// The equipped armour's content id, or empty when nothing is equipped. <b>The character-visual
    /// layer's binding source</b> — <see cref="HasArmour"/> cannot serve that role, because it is a
    /// bool: swapping one plate for another leaves it <c>true</c>, so R3 would raise no notification
    /// and the sprite would keep the old armour. AS3 avoids this by writing
    /// <c>Appear.ggArmorId</c> on <i>every</i> <c>changeArmor()</c> call (<c>UnitPlayer.as:3809</c>),
    /// not only when the armour goes on or off.
    ///
    /// <para>Written in <see cref="SetArmour"/>, the one place the projection is written, so it cannot
    /// fall out of step — including on break, where <c>ApplyDamage</c> routes through
    /// <see cref="UnequipArmour"/> and this drops to empty. That is what makes a broken plate's sprite
    /// come off, which AS3 gets from <c>changeArmor("off")</c> inside <c>Armor.damage()</c>
    /// (<c>Armor.as:331-334</c>).</para>
    /// </summary>
    public readonly ReactiveProperty<string> ArmourId = new(string.Empty);

    /// <summary>Base natural resistance, always applied. AS3 <c>Unit.skin</c>.</summary>
    public float skinResistance = 0f;

    // === Spell/boss shield (AS3 Unit.shithp / Unit.shitArmor) ===
    //
    // A damage-absorbing layer that sits in FRONT of the armour pool, distinct from it. Granted by the
    // sp_mshit spell (Spell.as:314-318) and set directly by four bosses (UnitAlicorn, UnitBossAlicorn,
    // UnitBossDron, UnitBossUltra). The armour workstream deliberately kept it out of the armour model
    // (2026-09-29 notes: "shithp/shitArmor is NOT armour — it is ... a boss/summoned shield layer").
    // The RULE lives in PFE.Systems.Magic.SpellShield; only the state lives here.

    /// <summary>
    /// The shield's own HP pool — AS3 <c>Unit.shithp</c> (<c>Unit.as:134</c>), default <b>0</b>
    /// (no shield). Worn down by the incoming damage of each hit while it is up; a hit that empties it
    /// drops the shield, and the ordinary armour path takes over on the next hit. Read by
    /// <see cref="PFE.Systems.Magic.SpellShield"/>.
    /// </summary>
    public float ShitHp = 0f;

    /// <summary>
    /// The shield's flat rating — AS3 <c>Unit.shitArmor</c> (<c>Unit.as:160</c>), default <b>20</b>,
    /// restored to 20 by <c>Pers.defaultParams()</c> (<c>Pers.as:882</c>). While <see cref="ShitHp"/>
    /// is positive it both reduces the hit (<c>Unit.as:3636</c>) and is subtracted from the armour
    /// pool's wear (<c>:3583</c>); a hit no larger than the rating is shrugged off entirely
    /// (<c>:3578</c>). Some bosses set it to 0 while the shield is up (<c>UnitTurret.as:507</c>).
    /// </summary>
    public float ShitArmor = 20f;

    // === Evasion (AS3 Unit.dexter / dexterPlus / dodge) ===
    //
    // The ranged/melee avoidance terms. Held here rather than on the definition because two of the
    // three are mutable at runtime — `dexterPlus` while the player sits or lurks, `dodge` while they
    // dash — and because `dexter` is level-scaled like `skin`. The definition's value is the baseline.

    /// <summary>
    /// Ranged evasion divisor — AS3 <c>Unit.dexter</c> (<c>Unit.as:166</c>), seeded from
    /// <c>@dexter</c> on the unit node. Defaults to <c>1</c>, which is AS3's own field default, so an
    /// unseeded unit evades at the baseline rather than not at all.
    /// </summary>
    public float dexterity = 1f;

    /// <summary>
    /// Flat addition to the ranged divisor — AS3 <c>Unit.dexterPlus</c>. Player sit/lurk only
    /// (<c>UnitPlayer.as:1117-1126</c>); 0 for an NPC, which is the oracle's default.
    /// </summary>
    public float dexterityPlus = 0f;

    /// <summary>
    /// Melee avoidance probability 0..1 — AS3 <c>Unit.dodge</c> (<c>Unit.as:170</c>). The player gets
    /// <c>1 + dodgePlus</c> while dashing (<c>UnitPlayer.as:1416-1422</c>); 0 for an NPC, which is why
    /// a club always lands on one.
    /// </summary>
    public float dodge = 0f;

    /// <summary>
    /// The evasion projection <see cref="PFE.Systems.Combat.IDamageable"/> exposes. A computed read of
    /// the three fields, so it cannot fall out of step with them — and a struct, so it allocates
    /// nothing on the hit path.
    /// </summary>
    public EvasionState Evasion => new EvasionState(dexterity, dexterityPlus, dodge);

    /// <summary>Scales the armour's flat reduction. AS3 <c>armorMult</c>. 1 = unmodified.</summary>
    public float armorEffectiveness = 1f;

    // === Derived: incoming-damage vulnerability (AS3 gg.vulner) ===

    /// <summary>
    /// The table <see cref="Vulnerabilities"/> is recomputed from. AS3 <c>Unit.begvulner</c>, the
    /// per-unit-id baseline the constructor fills from the unit's <c>&lt;vulner&gt;</c> element.
    ///
    /// <para>Defaults to <see cref="VulnerabilityData.Neutral"/>, which is <b>AS3's real baseline</b>
    /// and not a stand-in: <c>defaultParams()</c> fills the array with <c>1</c> and then forces
    /// <c>emp = 0</c> (<c>Pers.as:946-950</c>). It is also the correct value for the player
    /// specifically — there is no <c>id='player'</c> unit in <c>AllData.as</c>, so neutral <i>is</i>
    /// the player's table rather than a fallback for missing data.</para>
    /// </summary>
    private VulnerabilityData _vulnerabilityBaseline = VulnerabilityData.Neutral;

    /// <summary>
    /// The live incoming-damage multiplier table — AS3 <c>gg.vulner</c>, the array
    /// <c>Unit.damage():3529</c> multiplies every hit by.
    ///
    /// <para><b>Derived, never accumulated.</b> AS3 recomputes this from identity on every equipment
    /// or state change rather than nudging it: <c>setParameters()</c> (<c>Pers.as:2152</c>) calls
    /// <c>defaultParams()</c> to reset the whole block and only then re-applies each contributor, with
    /// <c>armorParameters(currentArmor)</c> at <c>:2212</c> and <c>armorParameters(currentAmul)</c> at
    /// <c>:2223</c>. That reset is what makes a plate swap <i>replace</i> its contribution; without it
    /// the old multiplier would be multiplied in again. See
    /// <see cref="RecalculateVulnerabilities"/>.</para>
    /// </summary>
    public VulnerabilityData Vulnerabilities { get; private set; } = VulnerabilityData.Neutral;

    /// <summary>
    /// Set the baseline and recompute. The seam a unit with both a <c>&lt;vulner&gt;</c> table and a
    /// <see cref="UnitStats"/> would use — today only the player has a <see cref="UnitStats"/>, and its
    /// baseline is the default, so production does not call this.
    /// </summary>
    public void SetVulnerabilityBaseline(VulnerabilityData baseline)
    {
        _vulnerabilityBaseline = baseline;
        RecalculateVulnerabilities();
    }

    /// <summary>
    /// Adopt a table as both the baseline and the live value — the seam an <b>effect</b>'s
    /// <c>tip='res'</c> write needs.
    ///
    /// <para><b>Why an effect must not go through <see cref="SetVulnerabilityBaseline"/>'s caller-side
    /// dictionary.</b> The player's skill/perk path builds its baseline from a dictionary that is reset
    /// at the top of every <c>RecalculateStats</c> (AS3 <c>defaultParams</c> zeroes the array), so a
    /// deduction recorded there is naturally transient. An effect's deduction needs the same property
    /// but comes through a different route: the effect's <c>&lt;sk tip='res'&gt;</c> writes
    /// <c>vulner[id] -= v</c> directly on the live table (<c>Unit.as:3443</c>), and the reset half of
    /// the effect pass (<see cref="RunEffectParamPass"/>) is what undoes it by rebuilding from the
    /// baseline.</para>
    ///
    /// <para>So the write must land on the <i>baseline</i> as well, or the very next pass would reset it
    /// away and the effect would flicker on and off once per tick. Adopting the modified table as the
    /// baseline is how "the effect changed the baseline" is expressed — and it is correct for the
    /// oracle's order too, because the effect pass runs <i>after</i> the skill/perk pass and therefore
    /// composes on top of it.</para>
    /// </summary>
    public void OverrideVulnerabilityBaseline(VulnerabilityData table)
    {
        _vulnerabilityBaseline = table;
        Vulnerabilities = table;
    }

    /// <summary>
    /// Rebuild <see cref="Vulnerabilities"/> from the baseline, then fold in each contributor — the
    /// port's <c>defaultParams()</c>-then-reapply pass, scoped to the one channel that has a consumer.
    ///
    /// <para><b>Only the equipped item model contributes.</b> AS3 folds armour <c>resist</c> into
    /// <c>gg.vulner</c> from <c>Pers.armorParameters()</c>, and <c>Pers</c> is the <i>player</i> class.
    /// An NPC is a <c>Unit</c>, whose <c>vulner</c> is set from its <c>&lt;vulner&gt;</c> element and
    /// then only ever touched by effects (<c>Unit.as:3466-3495</c>) — <c>Unit.damage()</c> never reads
    /// an armour <c>resist</c> array at all. So a <see cref="ArmourModel.UnitPool"/> projection must
    /// <b>not</b> contribute here, and excluding it is the oracle's split rather than a simplification.
    /// </para>
    /// </summary>
    private void RecalculateVulnerabilities()
    {
        VulnerabilityData live = _vulnerabilityBaseline;

        if (_armour.IsEquipped && _armour.model == ArmourModel.EquippedItem)
            live = live.WithResist(_armour.resists);

        Vulnerabilities = live;
    }

    // === UI Helpers ===
    public ReadOnlyReactiveProperty<float> HpPercent =>
        CurrentHp.CombineLatest(MaxHp, (current, max) =>
        {
            if (max <= 0) return 0;
            return current / max;
        }).ToReadOnlyReactiveProperty();

    // === Constructors ===
    public UnitStats(float maxHp, float maxMana)
    {
        MaxHp = new ReactiveProperty<float>(maxHp);
        CurrentHp = new ReactiveProperty<float>(maxHp);
        MaxMana = new ReactiveProperty<float>(maxMana);
        Mana = new ReactiveProperty<float>(maxMana);
    }

    public UnitStats() : this(100f, 100f) { }

    // === Methods ===
    public void Damage(float amount)
    {
        float newHp = Mathf.Clamp(CurrentHp.Value - amount, 0, MaxHp.Value);
        CurrentHp.Value = newHp;
    }

    public void Heal(float amount)
    {
        float newHp = Mathf.Clamp(CurrentHp.Value + amount, 0, MaxHp.Value);
        CurrentHp.Value = newHp;
    }

    /// <summary>
    /// Project an equipped armour <b>item</b> into combat stats and remember it, so its condition can
    /// be written back. This is the production entry point — AS3 <c>Armor.setArmor()</c>
    /// (<c>Armor.as:297-311</c>), which writes <c>owner.armor</c>/<c>marmor</c>/<c>armor_qual</c>.
    ///
    /// <para>The item is the source of truth for integrity, so the projection is taken from it here
    /// rather than handed in. Passing <c>null</c> unequips.</para>
    /// </summary>
    public void EquipArmour(IArmourItem item)
    {
        ArmourItem = item;
        SetArmour(item != null ? item.ToArmourState() : ArmourState.None);
    }

    /// <summary>
    /// Project a bare <see cref="ArmourState"/> with no backing item — the <b>unit pool</b> model, an
    /// NPC's own armour. There is nothing to write condition back to, because the pool <i>is</i> the
    /// state (AS3 <c>Unit.armor_hp</c>).
    /// </summary>
    public void EquipArmour(in ArmourState state)
    {
        ArmourItem = null;
        SetArmour(state);
    }

    /// <summary>
    /// Remove the armour projection.
    ///
    /// <para><b>Not a divergence — AS3 reaches the same end.</b> <c>changeArmor("off")</c> sets
    /// <c>active = false</c> and nulls the reference, and <c>setArmor()</c> is then guarded on
    /// <c>active</c> and does nothing — but <c>changeArmor</c> also runs <c>Pers.setParameters()</c>,
    /// which zeroes <c>gg.armor</c> and <c>gg.marmor</c> (<c>Pers.as:876-877</c>). An earlier revision
    /// of this comment called the clear a deliberate divergence and claimed AS3 kept a stale bonus; it
    /// does not.</para>
    /// </summary>
    public void UnequipArmour()
    {
        ArmourItem = null;
        SetArmour(ArmourState.None);
    }

    /// <summary>
    /// The one place <c>_armour</c> is written, so the reactive mirror cannot fall out of step with
    /// the projection. Every public mutator ends here.
    /// </summary>
    private void SetArmour(in ArmourState state)
    {
        _armour = state;
        ArmourIntegrity.Value = _armour.IntegrityPercent;
        HasArmour.Value = _armour.IsEquipped;

        // ArmourItem is already assigned by the caller before it gets here (EquipArmour sets it,
        // UnequipArmour clears it), so this reads the identity that matches the projection just
        // written. On a bare unit-pool projection ArmourItem is null, and an NPC has no armour
        // sprite to drive — so empty is the correct answer there, not a bug.
        ArmourId.Value = ArmourItem != null ? ArmourItem.Id : string.Empty;

        // The derived-stat pass. This is the port's `changeArmor()` hook: AS3 runs
        // `Pers.setParameters()` from there, which resets the derived block and re-applies every
        // contributor. Routing it through the one write point means equip, unequip and a plate-for-plate
        // swap all recompute, so a resistance is replaced rather than multiplied in again.
        RecalculateVulnerabilities();
    }

    /// <summary>
    /// Apply a resolved outcome: armour integrity first, then health.
    ///
    /// <para>This is the mutation half of the split. The formula
    /// (<c>DamageCalculator.ResolveDamage</c>) is pure and decides <i>what</i> happens; this decides
    /// nothing and only writes it down, on the entity that owns the state.</para>
    /// </summary>
    /// <returns><c>true</c> if this hit broke the armour (so the caller can report it).</returns>
    public bool ApplyDamage(in DamageOutcome outcome)
    {
        bool broke = false;

        if (outcome.ArmourIntegrityDamage > 0f && _armour.IsEquipped)
        {
            broke = _armour.TakeIntegrityDamage(outcome.ArmourIntegrityDamage);

            // Write the depleted condition back to the item BEFORE the break check unequips, so the
            // item records the hit that finished it. This is the design doc's step 8, and it is what
            // makes GameArmorInstance.Repair() and GameArmorSaveData.currentHealth mean anything —
            // without it the plate is depleted only inside a projection that gets thrown away.
            ArmourItem?.SetIntegrity(_armour.integrity);
            ArmourIntegrity.Value = _armour.IntegrityPercent;

            // AS3 Armor.damage() unequips on break (changeArmor("off"), Armor.as:331-334).
            if (broke) UnequipArmour();
        }

        if (outcome.HpDamage > 0f) Damage(outcome.HpDamage);

        return broke;
    }

    /// <summary>
    /// Repair the equipped armour. AS3 <c>Armor.repair()</c> (<c>Armor.as:341-347</c>) restores
    /// <c>hp</c>, clamps to <c>maxhp</c>, then re-projects through <c>setArmor()</c>.
    ///
    /// <para><b>A broken plate cannot be repaired through this method.</b> The breaking hit already
    /// ran <c>changeArmor("off")</c>, so nothing is equipped and there is nothing here to repair — AS3
    /// behaves the same way, because its <c>repair()</c> is called on the item and its <c>setArmor()</c>
    /// is guarded on <c>active</c>. The flow is <i>repair the item, then re-equip it</i>, which is the
    /// interaction layer's job (Phase 8): call <c>item.Repair(...)</c> and then
    /// <see cref="EquipArmour(IArmourItem)"/>.</para>
    ///
    /// <para><b>Why the <c>norep</c> check is repeated here.</b> This method repairs the <i>projection</i>
    /// and then writes the result straight to the item through <see cref="IArmourItem.SetIntegrity"/> —
    /// which does not pass through <c>GameArmorInstance.Repair</c>, so without this check a
    /// <c>norep</c> plate would be repairable here while being refused there. AS3 has no such split
    /// because it gates in the caller (<c>PipPageWork.as:258</c>); see <see cref="IArmourItem.CanRepair"/>
    /// for the full reasoning. Gating on both paths makes the two agree.</para>
    /// </summary>
    public void RepairArmour(float amount)
    {
        if (!_armour.IsEquipped || amount <= 0f) return;
        if (ArmourItem != null && !ArmourItem.CanRepair) return;

        _armour.Repair(amount);
        ArmourItem?.SetIntegrity(_armour.integrity);
        ArmourIntegrity.Value = _armour.IntegrityPercent;
    }

    // === Live status effects (AS3 Unit.effects) ===
    //
    // AS3 declares `public var effects:Array` on the base Unit class (Unit.as:494) and both the player
    // and every NPC own one. The port's `ActiveEffectSet` is that array plus `addEffect`/`remEffect`
    // and the effect half of `Unit.step`; it lives here because UnitStats is the one mutable object a
    // live unit of either kind holds.
    //
    // Built lazily rather than in the constructor: the set needs an `IEffectDefinitionResolver`, and
    // the resolver needs the content registry, which is a container-owned service this plain class
    // has no way to reach at construction. `EnsureEffects` is the seam — a spawner (or the player's
    // bootstrap) calls it once with the resolver; until then `Effects` answers an empty, resolver-less
    // set that refuses every id rather than throwing inside a tick.

    private ActiveEffectSet _effects;

    /// <summary>
    /// This unit's live status effects — AS3 <c>Unit.effects</c> (<c>Unit.as:494</c>).
    ///
    /// <para>Never <c>null</c>: <see cref="EnsureEffects"/> builds a resolver-less set on first read so
    /// a unit that was never handed a resolver still answers <see cref="ActiveEffectSet.Count"/> = 0
    /// instead of throwing. A resolver-less set adds nothing — see
    /// <see cref="ActiveEffectSet.AddEffect"/>, which refuses an id it cannot resolve rather than
    /// materialising a phantom, which is exactly the "plausible object with no behaviour" this project
    /// keeps finding.</para>
    /// </summary>
    public ActiveEffectSet Effects => _effects ??= new ActiveEffectSet(this, NullEffectDefinitions.Instance);

    /// <summary>
    /// Whether <see cref="EnsureEffects"/> has been given a real resolver. The debug readback uses this
    /// to distinguish "no effects" from "effects cannot resolve anything" — two states that look
    /// identical from <see cref="ActiveEffectSet.Count"/> alone.
    /// </summary>
    public bool HasEffectResolver { get; private set; }

    /// <summary>
    /// Hand this unit the resolver that maps an effect id to its template, and optionally the
    /// <see cref="PersMode"/> that decides how the param pass replays writes.
    ///
    /// <para>Idempotent. Re-wiring rebuilds the set, so it must be called before any effect is added —
    /// a spawner calls it at <c>Initialize</c> time. <paramref name="mode"/> is
    /// <see cref="PersMode.Player"/> for the player and <see cref="PersMode.Npc"/> otherwise, which is
    /// AS3's own split: <c>Unit.setEffParams</c> replays with index 1 (0 while being unset) and
    /// <c>Pers.setParameters</c> replays with <c>eff.lvl</c>, skipping removed effects entirely.</para>
    /// </summary>
    public void EnsureEffects(IEffectDefinitionResolver resolver, PersMode mode)
    {
        if (resolver == null)
        {
            return;
        }

        _effects = new ActiveEffectSet(this, resolver);
        EffectMode = mode;
        HasEffectResolver = true;
    }

    /// <summary>
    /// Which stack owns this unit's param replay — see <see cref="EnsureEffects"/>. Defaults to
    /// <see cref="PersMode.Npc"/>, which is the more conservative of the two: the NPC pass does not
    /// skip removed effects, so a value written by an effect is always undone by the replay.
    /// </summary>
    public PersMode EffectMode { get; private set; } = PersMode.Npc;

    /// <summary>
    /// The stat-write sink for an effect's <c>&lt;sk&gt;</c> params during the reset-then-replay pass.
    ///
    /// <para><b>Why a delegate and not a direct call.</b> The oracle splits this by owner: an NPC's
    /// params are written by <c>Unit.setSkillParam</c> onto the unit's own fields
    /// (<c>Unit.as:3413</c>), while the player's go through <c>Pers.setSkillParam</c> onto the
    /// character block (<c>Pers.as:1468</c>). The port's player block is <c>CharacterStats</c>, which
    /// <see cref="UnitStats"/> must not reference — the dependency runs the other way (<c>CharacterStats</c>
    /// holds a <c>UnitStats</c>). So the player's bootstrap assigns this once and the unit's own
    /// handler is the default.</para>
    ///
    /// <para>Signature: <c>(param, index, effectId)</c>. The <c>index</c> is the oracle's own
    /// per-mode level index — see <see cref="ActiveEffectSet.EnumerateParams"/> — and is passed
    /// through to <see cref="EffectParam.ValueForLevel(int)"/> by the sink, never recomputed.</para>
    /// </summary>
    public Action<EffectParam, int, string> EffectParamSink { get; set; }

    /// <summary>
    /// The <b>reset half</b> of the pass for an owner whose derived block this class cannot reach —
    /// AS3 <c>Pers.defaultParams()</c> as called from <c>setParameters()</c> (<c>Pers.as:2164</c>).
    ///
    /// <para><b>Why this exists, and what its absence broke.</b> The oracle's player pass is
    /// <i>reset-then-replay</i>: <c>Pers.setParameters</c> runs <c>defaultParams()</c> (which restores
    /// <c>gg.maxhp = begHP</c> and zeroes the whole derived block) and only then re-applies level,
    /// skills, perks and every effect at <c>eff.lvl</c>. The port had hoisted the replay here but never
    /// called the reset — <see cref="RunEffectParamPass"/> reset only its <i>own</i> vulnerability
    /// channel and replayed through <see cref="EffectParamSink"/>, so the player's
    /// <c>maxhp</c>/<c>skin</c>/… writes <b>compounded on every pass</b>. A comment claimed the reset
    /// happened "in <c>CharacterStats.ResetToDefaults</c>"; nothing called it from the effect path. That
    /// is this project's recurring shape — a comment describing a call that does not exist.</para>
    ///
    /// <para>Installed by the player's bootstrap together with <see cref="EffectParamSink"/>, and wired
    /// to <c>CharacterStats.RecalculateStats</c> (the port's <c>setParameters</c>: reset, level, skills,
    /// perks, trauma, sync — but <b>not</b> the effect replay, which stays here). A null reset sink
    /// means "this owner has no separate derived block", which is the NPC case: the oracle's
    /// <c>Unit.setEffParams</c> resets only vulnerability plus the three <c>Cont</c> counters, and an
    /// NPC-reachable effect writes nothing else — see <see cref="RunEffectParamPass"/>.</para>
    /// </summary>
    public Action EffectResetSink { get; set; }

    /// <summary>
    /// Whether the owner installed an <see cref="EffectResetSink"/>. Mirrors
    /// <see cref="HasEffectResolver"/>: the debug readback uses it to distinguish "an NPC, whose oracle
    /// path genuinely has no separate block to reset" from "a player whose reset never got wired" —
    /// two states that are indistinguishable from every stat value, because a missing reset only shows
    /// up as a value that is too high after a second pass.
    /// </summary>
    public bool HasEffectResetSink => EffectResetSink != null;

    /// <summary>
    /// The payload extension for a block <see cref="UnitStats"/> cannot reach — the player's organ
    /// heals (AS3 <c>Effect.as:401-403</c>, <c>pers.heal(val, 4)</c>/<c>(val, 5)</c>) and the future
    /// effect-emitter layer. Same shape as <see cref="EffectParamSink"/> and installed at the same
    /// place: the reference runs from <c>CharacterStats</c> to <c>UnitStats</c>, never back.
    ///
    /// <para>Called after <see cref="EffectPayloads.Run"/> has done the unit-side work, so a handler
    /// here must not repeat the plain damage or heal.</para>
    /// </summary>
    public Action<ActiveEffect> EffectPayloadSink { get; set; }

    /// <summary>
    /// Advance every live effect by one simulation tick — the effect half of AS3 <c>Unit.step</c>.
    ///
    /// <para><paramref name="stepScale"/> is <see cref="PFE.Core.SimClock.StepScale"/>, so a burn
    /// counts down in canonical 30 Hz frames regardless of the port's configured tick rate. Called
    /// once per unit step from <c>UnitController.StepUnit</c>, which is the shared body of both
    /// drivers — so a unit ticks once per step whether SimLoop or Unity's fixed clock drove it.</para>
    /// </summary>
    public void TickEffects(float stepScale)
    {
        _effects?.Tick(stepScale);
    }

    // ── IEffectHost ───────────────────────────────────────────────────────────

    /// <summary>
    /// An effect started — AS3 <c>Effect.setEff</c>'s param trigger (<c>Effect.as:157-168</c>).
    /// </summary>
    void IEffectHost.OnEffectStarted(ActiveEffect effect, ActiveEffectSet set)
    {
        // The oracle calls setParameters/setEffParams only when the effect carries <sk> params
        // (Effect.as:157, `if(this.params)`). An effect with no writes has nothing to replay, and
        // running the pass anyway would be free work on every tick of a burning unit.
        if (effect != null && effect.HasParams)
        {
            RunEffectParamPass();
        }
    }

    /// <summary>
    /// An effect truly left — the splice in <see cref="ActiveEffectSet.StepOnce"/>, not the
    /// <c>unsetEff</c> that marked it. The reset-then-replay pass has already undone its writes by
    /// then, because the set keeps a <c>vse</c> effect in place for one more frame exactly so its
    /// <c>v0</c> value can be replayed.
    /// </summary>
    void IEffectHost.OnEffectEnded(ActiveEffect effect, ActiveEffectSet set)
    {
        // Nothing to do here yet: the oracle's teardown tail (Effect.as:337-403) is the visual
        // teardown (stealth filter, potion parts, curse trigger) and the port has no visual layer for
        // effects. Left as an explicitly empty hook rather than removed, because the interface is
        // where a future visual pass plugs in — and an empty implementation that is *named* is
        // honest, whereas deleting the hook would make the next pass re-derive it.
    }

    /// <summary>
    /// Run an effect's per-second payload — AS3 <c>Effect.secEffect</c> (<c>Effect.as:405-472</c>).
    ///
    /// <para><b>Dispatches on id, as the oracle does.</b> The payload list is a hardcoded set of ids
    /// with bespoke behaviour and there is no data-driven table to port, so inventing one would be a
    /// divergence. The <see cref="PFE.Systems.Effects.EffectPayloads"/> dispatcher owns the mapping so
    /// both the NPC path here and any future player-specific payload share one implementation.</para>
    /// </summary>
    void IEffectHost.OnEffectPayload(ActiveEffect effect, ActiveEffectSet set)
    {
        EffectPayloads.Run(effect, this);
    }

    /// <summary>
    /// An effect's writes changed — the <c>setParameters()</c>/<c>setEffParams()</c> call the oracle
    /// makes from <c>setEff</c>, <c>unsetEff</c> and a level change (<c>Effect.as:241-248</c>).
    /// </summary>
    void IEffectHost.OnEffectParamsChanged(ActiveEffectSet set)
    {
        RunEffectParamPass();
    }

    /// <summary>
    /// The reset-then-replay pass, scoped to what this unit owns — the port of
    /// <c>Unit.setEffParams</c> (<c>Unit.as:3466-3496</c>) and its cousin
    /// <c>Pers.setParameters</c> (<c>Pers.as:2152-2205</c>).
    ///
    /// <para><b>The reset is the whole point.</b> The oracle does <i>not</i> nudge the derived block; it
    /// rebuilds it from identity and re-applies every contributor, because an effect's write has to be
    /// <i>removable</i>. Re-applying without a reset would compound every effect tick — a
    /// <c>+5 maxhp</c> that lands 30 times a second.</para>
    ///
    /// <para><b>Two channels, two resets, and they are not the same one.</b></para>
    /// <list type="number">
    /// <item><b>Vulnerability</b> — owned by this class. <see cref="RecalculateVulnerabilities"/>
    /// rebuilds it from <c>_vulnerabilityBaseline</c> (AS3's <c>vulner[i] = begvulner[i]</c> loop,
    /// <c>Unit.as:3480-3484</c>). This is the reset the <i>NPC</i> path actually needs, because every
    /// parameter an NPC-reachable effect writes is either a <c>tip='res'</c> index or one of the three
    /// <c>Cont</c> counters the oracle names explicitly — checked against all 79 <c>&lt;eff&gt;</c>
    /// rows: <c>contusion</c>/<c>freezing</c> write <c>tormoz</c>, <c>blindness</c>/<c>contusion</c>
    /// write <c>precMultCont</c>, <c>contusion</c> writes <c>rapidMultCont</c>, and the rest are
    /// <c>tip='res'</c>. The three counters have no home on this NPC-side object (the port keeps them
    /// on <c>CharacterStats</c>, which is the player block), so the NPC reset here is exactly this
    /// channel.</item>
    /// <item><b>The player's derived block</b> — owned by <c>CharacterStats</c>, reached through
    /// <see cref="EffectResetSink"/>. See that property for why its absence made a player buff
    /// compound.</item>
    /// </list>
    ///
    /// <para><b>A derived-stat write on the NPC path is deliberately not undone.</b> AS3's
    /// <c>setEffParams</c> never restores <c>maxhp</c>/<c>skin</c>/<c>dexter</c>. That is not an
    /// oversight to "fix": no NPC-reachable effect writes them (the <c>maxhp</c>/<c>skin</c> effects —
    /// <c>buck</c>, <c>f_hp</c>, the <c>f_*</c> foods — are player consumables replayed through
    /// <c>Pers.setParameters</c>), so the reset set is matched to the effect set. It matters only
    /// because the debug tab can apply an arbitrary effect to an arbitrary target: doing so to an NPC
    /// reproduces the oracle's own behaviour, which is that the write persists. Making it undo would be
    /// a divergence, not a fix.</para>
    ///
    /// <para><b>The index is per-mode and comes from the set, never computed here.</b> See
    /// <see cref="ActiveEffectSet.EnumerateParams"/> for the two paths.</para>
    /// </summary>
    private void RunEffectParamPass()
    {
        // Reset 1 — this owner's derived block, when it has one that is not this class. Must run
        // BEFORE the replay (AS3: defaultParams() at Pers.as:2164, then the eff loop at :2199).
        EffectResetSink?.Invoke();

        // Reset 2 — the vulnerability channel, so an effect's `tip='res'` write is replaced rather
        // than multiplied in again — AS3's `vulner[i] = begvulner[i]` loop (Unit.as:3480-3484).
        RecalculateVulnerabilities();

        foreach ((EffectParam param, int index) in Effects.EnumerateParams(EffectMode))
        {
            ApplyEffectParam(param, index);
        }
    }

    /// <summary>
    /// Apply one <c>&lt;sk&gt;</c> write — the port of <c>Unit.setSkillParam</c>
    /// (<c>Unit.as:3413-3460</c>) and <c>Pers.setSkillParam</c> (<c>Pers.as:1468</c>).
    ///
    /// <para><b><paramref name="index"/> is the oracle's level index, not necessarily the effect's
    /// level.</b> It arrives from <see cref="ActiveEffectSet.EnumerateParams"/> already resolved for
    /// this unit's mode; recomputing it from <see cref="ActiveEffect.Level"/> here would silently undo
    /// the NPC path's hardcoded 1.</para>
    ///
    /// <para>When a player sink is installed it takes over entirely, because the player's stat block is
    /// <c>CharacterStats</c> and this class cannot name it. Otherwise the unit's own fields are
    /// written, which is AS3's NPC behaviour.</para>
    /// </summary>
    private void ApplyEffectParam(in EffectParam param, int index)
    {
        if (EffectParamSink != null)
        {
            EffectParamSink(param, index, null);
            return;
        }

        float value = param.ValueForLevel(index);

        // AS3's `tip == "res"` branch — `this.vulner[id] -= value` (Unit.as:3443). Note the minus: the
        // vulnerability table is "1 = neutral, >1 = weak", so a positive value makes the target take
        // MORE of that damage type. The id is a DamageType index as a string.
        if (param.IsResistance)
        {
            if (int.TryParse(param.id, out int resIndex) &&
                Enum.IsDefined(typeof(DamageType), resIndex))
            {
                var live = Vulnerabilities;
                live.SetVulnerability((DamageType)resIndex,
                    live.GetVulnerability((DamageType)resIndex) - value);
                Vulnerabilities = live;
            }
            else
            {
                Effects.RecordUnmappedName("res:" + param.id);
            }
            return;
        }

        // The field-write branch. AS3 uses `hasOwnProperty` and treats an unknown name as a no-op
        // (Unit.as:3446); the port records it instead of inventing a field, which is the standing rule.
        switch (param.id)
        {
            case "maxhp":
                MaxHp.Value = ApplyOp(MaxHp.Value, param.op, value);
                break;
            case "skin":
                skinResistance = ApplyOp(skinResistance, param.op, value);
                break;
            case "dexter":
                dexterity = ApplyOp(dexterity, param.op, value);
                break;
            case "dodgePlus":
                dodge = ApplyOp(dodge, param.op, value);
                break;
            case "armorMult":
            case "armorEffectiveness":
                armorEffectiveness = ApplyOp(armorEffectiveness, param.op, value);
                break;
            default:
                Effects.RecordUnmappedName(param.id);
                return;
        }
    }

    /// <summary>
    /// AS3's three write operators on a field: <c>add</c>, <c>mult</c>, or a plain assign
    /// (<c>Unit.as:3448-3459</c>). Kept here rather than on <see cref="EffectParam"/>, which is a
    /// serialized struct and should not carry behaviour that reads a runtime value.
    /// </summary>
    private static float ApplyOp(float current, EffectParamRef op, float value)
    {
        switch (op)
        {
            case EffectParamRef.Add: return current + value;
            case EffectParamRef.Mult: return current * value;
            default: return value;
        }
    }

    /// <summary>
    /// The resolver a unit that was never handed one gets — answers <c>null</c> for every id, which
    /// makes <see cref="ActiveEffectSet.AddEffect"/> refuse rather than build a phantom. A named type
    /// rather than a lambda so the intent is greppable.
    /// </summary>
    private sealed class NullEffectDefinitions : IEffectDefinitionResolver
    {
        public static readonly NullEffectDefinitions Instance = new NullEffectDefinitions();

        public IEffectTemplate Resolve(string effectId) => null;
    }

    public bool IsAlive => CurrentHp.Value > 0;
    public bool IsDead => CurrentHp.Value <= 0;
}

}