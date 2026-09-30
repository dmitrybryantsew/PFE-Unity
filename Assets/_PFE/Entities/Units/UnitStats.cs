using R3;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Combat;

namespace PFE.Entities.Units
{
/// <summary>
/// Reactive stats system for units.
/// Acts as the data container for both RPG stats and Combat calculations.
/// </summary>
public class UnitStats
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

    public bool IsAlive => CurrentHp.Value > 0;
    public bool IsDead => CurrentHp.Value <= 0;
}

}