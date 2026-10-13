using System;
using PFE.Entities.Units;
using PFE.Systems.Inventory;
using PFE.Systems.RPG;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// Reads the status monitor's 15 rows out of the live player.
    ///
    /// <para><b>Every source is a <c>Func</c>, and that is deliberate.</b> The monitor's rows resolve
    /// against six different owners (the player's <c>UnitStats</c>, <c>CharacterStats</c>, the
    /// equipped weapon, the equipped armour, a pet, and <c>GameInventory</c>), and several of them do
    /// not exist at boot — the weapon is created in <c>PlayerWeaponLoadout.Start</c>, which Unity does
    /// not order against this. A constructor that captured concrete instances would bind null once and
    /// then show zeroes forever, which is the exact failure <c>HudBootstrapper</c> documents. Resolving
    /// per read means a weapon equipped a second later simply appears.</para>
    ///
    /// <para><b>Rows with no source stay hidden rather than showing zero.</b> The Oracle hides a row by
    /// passing <c>(0, 0, false)</c> to its <c>ritem*</c> helper; a port that rendered <c>0 / 0</c>
    /// instead would put a plausible-looking reading on screen for a system that does not exist. Every
    /// provider below therefore defaults to "no value", and the bootstrapper opts each one in as its
    /// system lands.</para>
    /// </summary>
    public sealed class PlayerPipStatusSource : IPipStatusSource
    {
        private readonly Func<CharacterStats> _characterStats;
        private readonly Func<UnitStats> _unitStats;
        private readonly Func<GameInventory> _inventory;

        public PlayerPipStatusSource(
            Func<CharacterStats> characterStats,
            Func<UnitStats> unitStats,
            Func<GameInventory> inventory)
        {
            _characterStats = characterStats;
            _unitStats = unitStats;
            _inventory = inventory;
        }

        // ── Optional providers ────────────────────────────────────────────────
        //
        // Left null until the owning system exists. A null provider means "this row has no source",
        // which the Oracle expresses as a hidden row — not as a zero.

        /// <summary>
        /// The <c>nomed</c> trigger, inverted. The Oracle reads
        /// <c>!World.w.game.triggers["nomed"]</c> (<c>PipBuck.as:510</c>); the port has no trigger table
        /// wired here yet, so this defaults to <c>true</c> (organs shown), which matches a fresh game.
        /// </summary>
        public Func<bool> MedicineEnabledProvider { get; set; }

        /// <summary>Weapon condition, or <c>has == false</c> to hide the row.</summary>
        public Func<(bool has, float hp, float max)> WeaponConditionProvider { get; set; }

        /// <summary>Armour condition, or <c>has == false</c> to hide the row.</summary>
        public Func<(bool has, float hp, float max)> ArmourConditionProvider { get; set; }

        /// <summary>Pet health, or <c>has == false</c> to hide the row.</summary>
        public Func<(bool has, float hp, float max)> PetProvider { get; set; }

        /// <summary>
        /// Caps, for row 9. <b>No provider is registered by default because the port does not track
        /// money at all</b> — <c>GameInventory.cs:71</c> records it as deliberately untracked
        /// ("cat0: Not tracked (money, special)"). The row therefore shows 0, which is the Oracle's own
        /// starting value (<c>PipBuck.money = 0</c>, <c>PipBuck.as:51</c>) rather than a guess.
        /// </summary>
        public Func<int> CapsProvider { get; set; }

        public PipStatusRow[] Snapshot()
        {
            var snapshot = PipStatusSnapshot.Minimal;

            CharacterStats stats = _characterStats?.Invoke();
            if (stats != null)
            {
                snapshot.HeadHp = stats.headHp;
                snapshot.TorsoHp = stats.torsHp;
                snapshot.LegsHp = stats.legsHp;
                snapshot.BloodHp = stats.bloodHp;
                snapshot.ManaOrganHp = stats.manaHp;
                snapshot.InMaxHp = stats.InMaxHP;
                snapshot.InMaxMana = stats.MaxMana;

                snapshot.MaxWeaponMass = stats.maxmW;
                snapshot.MaxMagicMass = stats.maxmM;
                snapshot.MaxMass1 = stats.maxm1;
                snapshot.MaxMass2 = stats.maxm2;
                snapshot.MaxMass3 = stats.maxm3;
            }

            UnitStats unit = _unitStats?.Invoke();
            if (unit != null)
            {
                snapshot.Hp = unit.CurrentHp.Value;
                snapshot.MaxHp = unit.MaxHp.Value;
            }

            GameInventory inventory = _inventory?.Invoke();
            if (inventory != null)
            {
                snapshot.HardInventory = inventory.HardInventory;
                snapshot.WeaponMass = inventory.WeaponMass;
                snapshot.MagicMass = inventory.MagicMass;

                // The Oracle's own maxima are the player's (`Pers.maxmW` / `maxmM`), not the
                // inventory's; GameInventory carries fallbacks (100 / 50) for when the character
                // stats are not available. Preferring the stats above and falling back here keeps a
                // single denominator rather than two disagreeing ones.
                if (stats == null)
                {
                    snapshot.MaxWeaponMass = inventory.MaxWeaponMass;
                    snapshot.MaxMagicMass = inventory.MaxMagicMass;
                }

                // Categories 1..3 are the Oracle's `inv.mass[1..3]`; index 0 is the untracked
                // money/special bucket, which is why the offsets are +1 rather than 0..2.
                snapshot.Mass1 = inventory.GetCategoryMass(1);
                snapshot.Mass2 = inventory.GetCategoryMass(2);
                snapshot.Mass3 = inventory.GetCategoryMass(3);
            }

            if (MedicineEnabledProvider != null)
            {
                snapshot.MedicineEnabled = MedicineEnabledProvider();
            }

            ApplyCondition(WeaponConditionProvider, out bool hasWeapon, out float weaponHp, out float weaponMax);
            snapshot.WeaponHasCondition = hasWeapon;
            snapshot.WeaponHp = weaponHp;
            snapshot.WeaponMaxHp = weaponMax;

            ApplyCondition(ArmourConditionProvider, out bool hasArmour, out float armourHp, out float armourMax);
            snapshot.HasArmour = hasArmour;
            snapshot.ArmourHp = armourHp;
            snapshot.ArmourMaxHp = armourMax;

            if (PetProvider != null)
            {
                (bool has, float hp, float max) pet = PetProvider();
                snapshot.HasPet = pet.has;
                snapshot.PetHp = pet.hp;
                snapshot.PetMaxHp = pet.max;
            }

            if (CapsProvider != null)
            {
                snapshot.Caps = CapsProvider();
            }

            return PipStatusRows.Build(snapshot);
        }

        /// <summary>
        /// Reads a condition provider defensively. A provider that throws (because its owner was
        /// destroyed between the null check and the call) hides the row instead of taking the whole
        /// monitor down with it — the panel is on screen whenever the device is open, so an exception
        /// here would spam the log every frame.
        /// </summary>
        private static void ApplyCondition(Func<(bool has, float hp, float max)> provider,
            out bool has, out float hp, out float max)
        {
            has = false;
            hp = 0f;
            max = 0f;

            if (provider == null) return;

            try
            {
                (bool h, float v, float m) = provider();
                has = h;
                hp = v;
                max = m;
            }
            catch (Exception)
            {
                has = false;
            }
        }
    }
}
