using R3;
using UnityEngine;

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
    public float armor = 0f;
    public float armorEffectiveness = 1f;

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

    public bool IsAlive => CurrentHp.Value > 0;
    public bool IsDead => CurrentHp.Value <= 0;
}

}