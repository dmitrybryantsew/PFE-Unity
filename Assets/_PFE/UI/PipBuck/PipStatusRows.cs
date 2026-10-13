using System;
using PFE.Systems.Inventory;

namespace PFE.UI.PipBuck
{
    /// <summary>How a status-panel row renders its value. Mirrors the Oracle's three <c>ritem*</c> shapes.</summary>
    public enum PipStatusRowKind
    {
        /// <summary>The row is not shown. The Oracle's <c>ritemN(i, 0, 0, false)</c>.</summary>
        Hidden,

        /// <summary><c>ritem1</c> — <c>"v / max"</c>, with the threshold colour on <c>v</c>.</summary>
        Value,

        /// <summary><c>ritem2</c> — <c>"N%"</c>, with the threshold colour.</summary>
        Percent,

        /// <summary><c>ritem3</c> — <c>"v / max"</c>, red only when <c>v &gt; max</c>.</summary>
        Mass,

        /// <summary>The caps row — a bare amount in the <c>yel</c> class.</summary>
        Caps,
    }

    /// <summary>One row of the 15-row status monitor.</summary>
    public readonly struct PipStatusRow
    {
        public PipStatusRow(PipStatusRowKind kind, float value, float max)
        {
            Kind = kind;
            Value = value;
            Max = max;
        }

        public PipStatusRowKind Kind { get; }
        public float Value { get; }
        public float Max { get; }

        public bool Visible => Kind != PipStatusRowKind.Hidden;

        /// <summary>The Oracle's hidden row.</summary>
        public static PipStatusRow Hidden => new PipStatusRow(PipStatusRowKind.Hidden, 0f, 0f);

        /// <summary>Renders the row with the Oracle's own formatting.</summary>
        public string Format()
        {
            switch (Kind)
            {
                case PipStatusRowKind.Value: return PipBuckReadoutFormat.Value(Value, Max);
                case PipStatusRowKind.Percent: return PipBuckReadoutFormat.Percent(Value, Max);
                case PipStatusRowKind.Mass: return PipBuckReadoutFormat.Mass(Value, Max);
                case PipStatusRowKind.Caps: return PipBuckReadoutFormat.Caps((int)Value);
                default: return string.Empty;
            }
        }
    }

    /// <summary>
    /// Where the status monitor's numbers come from. An interface so the row-building rules can be
    /// executed offline against a hand-made source, with no player and no <c>ScriptableObject</c>.
    /// </summary>
    public interface IPipStatusSource
    {
        /// <summary>Exactly <see cref="PipBuckLayout.MonitorRowCount"/> rows, in the Oracle's order.</summary>
        PipStatusRow[] Snapshot();
    }

    /// <summary>
    /// Builds the 15 rows from the Oracle's rule set, given a plain data snapshot.
    ///
    /// <para><b>The row order is the contract.</b> <c>PipBuck.setRPanel</c> (<c>PipBuck.as:501-545</c>)
    /// fills them positionally, and the icon for each row is frame <c>i+1</c> of the <c>trol</c> clip
    /// (<c>PipBuck.as:141</c>). So row <i>n</i> and icon <i>n</i> are the same thing, and getting the
    /// order wrong would silently mislabel every reading below the mistake. It is spelled out once,
    /// here, rather than left implicit in an array literal.</para>
    /// </summary>
    public static class PipStatusRows
    {
        public const int Hp = 0;
        public const int Head = 1;
        public const int Torso = 2;
        public const int Legs = 3;
        public const int Blood = 4;
        public const int ManaOrgan = 5;
        public const int Pet = 6;
        public const int WeaponCondition = 7;
        public const int ArmourCondition = 8;
        public const int Caps = 9;
        public const int WeaponMass = 10;
        public const int MagicMass = 11;
        public const int CategoryMass1 = 12;
        public const int CategoryMass2 = 13;
        public const int CategoryMass3 = 14;

        /// <summary>
        /// The Oracle's row labels, in row order. Mirrors
        /// <c>PipBuckCompositionDefinition.MonitorRowLabels</c>, which names the <c>trol</c> frames.
        /// </summary>
        public static readonly string[] Labels =
        {
            "HP", "Head", "Torso", "Legs", "Blood", "Mana", "Pet",
            "Weapon", "Armour", "Caps", "WeaponMass", "MagicMass",
            "Mass1", "Mass2", "Mass3",
        };

        /// <summary>
        /// Builds every row from a snapshot of the player's state.
        ///
        /// <para><b>Visibility follows the Oracle exactly</b> (<c>PipBuck.as:509-544</c>):</para>
        /// <list type="bullet">
        /// <item><description>Rows 1-5 (the organs) hide together when the <c>nomed</c> trigger is set —
        /// one flag, five rows.</description></item>
        /// <item><description>Row 6 (pet) hides when there is no pet, and the Oracle passes
        /// <c>(0, 0, false)</c> rather than an empty pet's stats.</description></item>
        /// <item><description>Row 7 (weapon condition) shows only for <c>tip &lt;= 3</c> — i.e. not for
        /// thrown or magic weapons.</description></item>
        /// <item><description>Row 8 (armour) hides with nothing equipped.</description></item>
        /// <item><description>Rows 10-14 (all five mass rows) are gated on
        /// <c>World.w.hardInv</c> — the weight system is only enforced in hard mode.</description></item>
        /// </list>
        /// </summary>
        public static PipStatusRow[] Build(PipStatusSnapshot s)
        {
            var rows = new PipStatusRow[PipBuckLayout.MonitorRowCount];

            rows[Hp] = new PipStatusRow(PipStatusRowKind.Value, s.Hp, s.MaxHp);

            // PipBuck.as:510-514 — all five organs share `inMaxHP` and the `nomed` gate.
            PipStatusRow organ(float value) => s.MedicineEnabled
                ? new PipStatusRow(PipStatusRowKind.Value, value, s.InMaxHp)
                : PipStatusRow.Hidden;

            rows[Head] = organ(s.HeadHp);
            rows[Torso] = organ(s.TorsoHp);
            rows[Legs] = organ(s.LegsHp);
            rows[Blood] = organ(s.BloodHp);

            // The mana organ is the odd one out: same `nomed` gate, but its own ceiling (`inMaxMana`),
            // not `inMaxHP`. PipBuck.as:514.
            rows[ManaOrgan] = s.MedicineEnabled
                ? new PipStatusRow(PipStatusRowKind.Value, s.ManaOrganHp, s.InMaxMana)
                : PipStatusRow.Hidden;

            // PipBuck.as:515-522.
            rows[Pet] = s.HasPet
                ? new PipStatusRow(PipStatusRowKind.Value, s.PetHp, s.PetMaxHp)
                : PipStatusRow.Hidden;

            // PipBuck.as:523-530 — `tip <= 3` excludes thrown (4) and magic (5) weapons, which have no
            // condition to report.
            rows[WeaponCondition] = s.WeaponHasCondition
                ? new PipStatusRow(PipStatusRowKind.Percent, s.WeaponHp, s.WeaponMaxHp)
                : PipStatusRow.Hidden;

            // PipBuck.as:531-538.
            rows[ArmourCondition] = s.HasArmour
                ? new PipStatusRow(PipStatusRowKind.Percent, s.ArmourHp, s.ArmourMaxHp)
                : PipStatusRow.Hidden;

            // PipBuck.as:539 — written inline, not through ritem*, so it has no threshold colour.
            rows[Caps] = new PipStatusRow(PipStatusRowKind.Caps, s.Caps, 0f);

            // PipBuck.as:540-544 — all five gated on hardInv.
            if (s.HardInventory)
            {
                rows[WeaponMass] = new PipStatusRow(PipStatusRowKind.Mass, s.WeaponMass, s.MaxWeaponMass);
                rows[MagicMass] = new PipStatusRow(PipStatusRowKind.Mass, s.MagicMass, s.MaxMagicMass);
                rows[CategoryMass1] = new PipStatusRow(PipStatusRowKind.Mass, s.Mass1, s.MaxMass1);
                rows[CategoryMass2] = new PipStatusRow(PipStatusRowKind.Mass, s.Mass2, s.MaxMass2);
                rows[CategoryMass3] = new PipStatusRow(PipStatusRowKind.Mass, s.Mass3, s.MaxMass3);
            }

            return rows;
        }
    }

    /// <summary>
    /// A plain snapshot of everything the status monitor needs. A struct-of-fields rather than a set of
    /// delegates so <see cref="PipStatusRows.Build"/> is a pure function and can be tested by
    /// construction.
    /// </summary>
    public struct PipStatusSnapshot
    {
        public float Hp, MaxHp;

        /// <summary><c>Pers.headHP</c> and siblings — the four limb/blood organs.</summary>
        public float HeadHp, TorsoHp, LegsHp, BloodHp;

        /// <summary><c>Pers.manaHP</c> — the mana <i>organ</i>, not the casting budget.</summary>
        public float ManaOrganHp;

        /// <summary>Denominator for the four organs (<c>Pers.inMaxHP</c>).</summary>
        public float InMaxHp;

        /// <summary>Denominator for the mana organ (<c>Pers.inMaxMana</c>).</summary>
        public float InMaxMana;

        /// <summary>False when the <c>nomed</c> trigger is set — hides all five organ rows at once.</summary>
        public bool MedicineEnabled;

        public bool HasPet;
        public float PetHp, PetMaxHp;

        public bool WeaponHasCondition;
        public float WeaponHp, WeaponMaxHp;

        public bool HasArmour;
        public float ArmourHp, ArmourMaxHp;

        public int Caps;

        public bool HardInventory;
        public float WeaponMass, MaxWeaponMass;
        public float MagicMass, MaxMagicMass;
        public float Mass1, MaxMass1;
        public float Mass2, MaxMass2;
        public float Mass3, MaxMass3;

        /// <summary>
        /// A snapshot with every optional row hidden and the mandatory ones at full health. Useful as a
        /// test baseline: anything visible in the result is there because a flag was set.
        /// </summary>
        public static PipStatusSnapshot Minimal => new PipStatusSnapshot
        {
            Hp = 100f,
            MaxHp = 100f,
            InMaxHp = 100f,
            InMaxMana = 100f,
            MedicineEnabled = true,
            HeadHp = 100f,
            TorsoHp = 100f,
            LegsHp = 100f,
            BloodHp = 100f,
            ManaOrganHp = 100f,
        };
    }
}
