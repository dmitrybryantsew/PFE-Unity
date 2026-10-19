using System;
using PFE.Entities.Units;
using PFE.Systems.Inventory;
using PFE.Systems.RPG;
using PFE.Systems.Trade;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// Where the doctor's page reads the player from, and the one place it writes back — the seam the
    /// page is built against so its arithmetic can be exercised with a hand-made snapshot: no player, no
    /// <c>ScriptableObject</c>, no <c>MonoBehaviour</c>.
    ///
    /// <para><b>Two methods, because the Oracle's page has exactly two directions.</b>
    /// <c>setSubPages</c> only reads (<c>PipPageMed.as:63-192</c>); <c>itemClick</c> charges the purse
    /// and heals an organ (<c>:278-450</c>). Splitting them keeps the write on one named call instead of
    /// letting a view reach into the RPG system from three branches.</para>
    /// </summary>
    public interface IPipMedSource
    {
        /// <summary>Everything the page reads, as plain data. Never null.</summary>
        PipMedSnapshot Read();

        /// <summary>
        /// Charges <see cref="PipMedPlan.Cost"/> to the purse and applies
        /// <see cref="PipMedPlan.Heal"/>. Returns false when the treatment could not be applied —
        /// either the purse refused the charge or the port has no destination for that reading.
        /// </summary>
        bool TryApply(in PipMedPlan plan);
    }

    /// <summary>
    /// Reads the doctor's page out of the live player and applies its treatments — the port of
    /// <c>PipPageMed</c>'s two data paths.
    ///
    /// <para><b>Every owner is a <see cref="Func{TResult}"/>, for the reason the other PipBuck sources
    /// record.</b> The player is assembled in <c>PlayerController.Awake</c>, which Unity does not order
    /// against the bootstrapper's <c>Start</c>; a constructor that captured instances would bind null
    /// once and show an empty price list forever.</para>
    ///
    /// <para><b>What the port cannot read produces no row, never a zero.</b> Radiation, open wounds and
    /// poison have no member anywhere in the port — <c>PipStatVitals.HasExposureReadings</c> is false for
    /// the same reason — so <see cref="Read"/> leaves
    /// <see cref="PipMedSnapshot.HasExposureReadings"/> false and the page lists five organs and a health
    /// pool instead of printing three zeroes at a price of zero.</para>
    /// </summary>
    public sealed class PlayerPipMedSource : IPipMedSource
    {
        private readonly Func<CharacterStats> _characterStats;
        private readonly Func<UnitStats> _unitStats;
        private readonly Func<GameInventory> _inventory;

        public PlayerPipMedSource(
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
        // Left null until the owning system exists, and each null has a stated default rather than a
        // guess. Same arrangement as PlayerPipStatSource's.

        /// <summary>
        /// <c>pip.npcInter</c> — which doctor is open, and therefore which row the purse is
        /// (<c>PipPageMed.as:48-62</c>). Null reads empty, which is the plain caps-paying <c>doc</c>.
        /// </summary>
        public Func<string> NpcInterProvider { get; set; }

        /// <summary>
        /// <c>World.w.game.globalDif</c> — the index into <see cref="PipMedPrices.ForDifficulty"/>.
        /// Null uses <see cref="DefaultDifficulty"/>, the Oracle's own default (<c>Game.as:41</c>).
        /// </summary>
        public Func<int> GlobalDifficultyProvider { get; set; }

        /// <summary>
        /// <c>pip.noAct</c> — the device is not accepting actions. Null means "it is", which is the
        /// truth for every port path that can open the clinic: none of them runs during a cutscene.
        /// </summary>
        public Func<bool> ActionsBlockedProvider { get; set; }

        /// <summary>
        /// The doctor's own wallet — <c>pip.vendor</c> in the Oracle. A caps-paying doctor's takings are
        /// credited to it (<c>PipPageMed.as:442-445</c>), which is why the same doctor can later afford
        /// to buy from the player. Null leaves the takings uncredited, which is a real difference and is
        /// named in <see cref="TryApply"/> rather than hidden.
        /// </summary>
        public Func<VendorRuntime> ClinicVendorProvider { get; set; }

        /// <summary><c>Game.as:41</c> — <c>globalDif</c>'s default, the port's own Normal tier.</summary>
        public const int DefaultDifficulty = 2;

        /// <summary>The item row a caps-paying doctor is paid in — <c>Invent.money</c>.</summary>
        public const string MoneyItemId = GameInventory.MoneyItemId;

        /// <summary>The item row an <c>adoc</c> is paid in (<c>PipPageMed.as:51</c>).</summary>
        public const string GelItemId = "gel";

        /// <summary>The item row a <c>vdoc</c> is paid in (<c>PipPageMed.as:56</c>).</summary>
        public const string GoodItemId = "good";

        /// <summary>
        /// Which row a doctor of this kind is paid in — <c>PipPageMed.as:48-62</c>. An <c>adoc</c> takes
        /// gel, a <c>vdoc</c> takes goods, and everything else (a <c>doc</c>, and any NPC that reaches
        /// the clinic another way) takes caps.
        /// </summary>
        public static string CurrencyFor(string npcInter)
        {
            if (npcInter == "adoc") return GelItemId;
            if (npcInter == "vdoc") return GoodItemId;
            return MoneyItemId;
        }

        /// <summary>The prices the open doctor charges — <see cref="PipMedPrices.ForDifficulty"/>.</summary>
        public PipMedPrices Prices =>
            PipMedPrices.ForDifficulty(GlobalDifficultyProvider != null
                ? GlobalDifficultyProvider()
                : DefaultDifficulty);

        // ── Read ──────────────────────────────────────────────────────────────

        public PipMedSnapshot Read()
        {
            CharacterStats stats = _characterStats?.Invoke();
            UnitStats unit = _unitStats?.Invoke();
            GameInventory inventory = _inventory?.Invoke();
            string currency = CurrencyFor(NpcInterProvider?.Invoke());

            var snapshot = new PipMedSnapshot
            {
                HasPlayer = stats != null || unit != null,
                Prices = Prices,
                Level = stats != null ? stats.Level : 0,

                // `gg.hp` / `gg.maxhp` are the UNIT's pool (:88), the same split the character sheet
                // makes: the organs are Pers's and the pool is the Unit's.
                Hp = unit != null ? unit.CurrentHp.Value : 0f,
                MaxHp = unit != null ? unit.MaxHp.Value : 0f,

                HeadHp = stats != null ? stats.headHp : 0f,
                TorsoHp = stats != null ? stats.torsHp : 0f,
                LegsHp = stats != null ? stats.legsHp : 0f,
                BloodHp = stats != null ? stats.bloodHp : 0f,
                ManaOrganHp = stats != null ? stats.manaHp : 0f,

                InMaxHp = stats != null ? stats.InMaxHP : 0f,
                InMaxMana = stats != null ? stats.MaxMana : 0f,

                HeadSt = stats != null ? stats.headSt : 0,
                TorsoSt = stats != null ? stats.torsSt : 0,
                LegsSt = stats != null ? stats.legsSt : 0,
                BloodSt = stats != null ? stats.bloodSt : 0,
                ManaSt = stats != null ? stats.manaSt : 0,

                // `gg.rad` / `gg.cut` / `gg.poison` have no port member. The Oracle pushes these three
                // rows unconditionally (:172-192); the flag is what stops the page pricing readings
                // nothing takes. See PipStatVitals.HasExposureReadings for the same decision on the
                // character sheet's Health sub-page.
                HasExposureReadings = false,

                CurrencyItemId = currency,
                CurrencyBalance = BalanceOf(inventory, currency),

                ActionsBlocked = ActionsBlockedProvider != null && ActionsBlockedProvider(),
            };

            return snapshot;
        }

        /// <summary>
        /// <c>this.plata.kol</c> — the quantity of the currency row. Caps go through
        /// <see cref="GameInventory.Money"/> because that property <i>is</i> the <c>items["money"]</c>
        /// row and it also raises <see cref="GameInventory.MoneyChanged"/>; gel and goods are ordinary
        /// rows with no such facade.
        /// </summary>
        private static int BalanceOf(GameInventory inventory, string currency)
        {
            if (inventory == null) return 0;
            if (currency == MoneyItemId) return inventory.Money;

            GameItemInstance row = inventory.GetItem(currency);
            return row != null ? row.Quantity : 0;
        }

        // ── Write ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Charges the purse and applies the treatment — <c>PipPageMed.itemClick</c>'s two side effects
        /// (<c>:296-297</c> and every sibling: <c>gg.heal(...)</c> then <c>this.plata.kol -= Math.round(_loc2_)</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>The charge rounds, and the round is AS3's.</b> <c>Math.round(_loc2_)</c> on the way
        /// out of the purse, not <c>ceil</c> — a 1.4-cap treatment costs 1 cap. Note the <i>heal</i> uses
        /// the unrounded <c>_loc2_</c>, so the two sides are deliberately not the same number: the
        /// player gets the HP they paid the fractional price for and pays the whole cap. Reproduced
        /// rather than tidied, because it is the price the game charges.</para>
        ///
        /// <para><b>Order: heal first, then charge.</b> The Oracle heals before it subtracts
        /// (<c>:296-297</c>), and the order is observable — a charge that failed would leave the heal in
        /// place. The port keeps it, and charges <i>after</i> a successful heal, so a refused treatment
        /// never costs anything.</para>
        /// </remarks>
        public bool TryApply(in PipMedPlan plan)
        {
            if (!plan.Acts) return false;

            int charge = PipBuckReadoutFormat.As3Round(plan.Cost);

            if (plan.Target == PipMedTarget.Rad || plan.Target == PipMedTarget.Cut ||
                plan.Target == PipMedTarget.Poison)
            {
                // `gg.heal(n, 2|3|4)` writes `Unit.rad`, `cut` and `poison` (UnitPlayer.as:3171-3201).
                // The port keeps none of the three, so there is nothing to heal and nothing is charged.
                // Unreachable while Read() reports HasExposureReadings false; kept so that a snapshot
                // which turns the flag on cannot silently take the player's money.
                UnityEngine.Debug.LogWarning(
                    $"[PlayerPipMedSource] '{plan.Target}' treatment: the Oracle heals it through " +
                    "`UnitPlayer.heal(_, 2|3|4)`, but the port keeps no radiation, wound or poison " +
                    "value (UnitPlayer.as:3171-3201). Nothing was healed and nothing was charged.");
                return false;
            }

            if (plan.Target == PipMedTarget.Hp)
            {
                UnitStats unit = _unitStats?.Invoke();
                if (unit == null) return false;

                // `gg.heal(amount, 0, false)` (UnitPlayer.as:3155-3166) clamps to `maxhp - rad`; with no
                // rad that is the ceiling itself. The third argument is `false`, i.e. no floating
                // number — the Oracle suppresses it for a doctor's treatment, which the port has no
                // damage-number emitter for anyway.
                if (!TryCharge(plan.SpendsMoney, charge)) return false;
                unit.Heal(plan.Heal);
                CreditClinic(plan, charge);
                return true;
            }

            // The organs: `gg.pers.heal(amount, 1|2|3|5|6)` (Pers.as:1863-1942), which also recomputes
            // the organ's trauma stage and calls setParameters() when the stage changes.
            CharacterStats stats = _characterStats?.Invoke();
            if (stats == null) return false;

            if (!TryCharge(plan.SpendsMoney, charge)) return false;
            stats.HealOrgan(plan.Heal, plan.OrganType);
            CreditClinic(plan, charge);
            return true;
        }

        /// <summary>
        /// <c>this.plata.kol -= Math.round(_loc2_)</c>. Caps go through
        /// <see cref="GameInventory.TrySpendMoney"/> so the caps row's change event fires; gel and goods
        /// are ordinary rows.
        /// </summary>
        private bool TryCharge(bool spendsMoney, int charge)
        {
            if (charge <= 0) return true;

            GameInventory inventory = _inventory?.Invoke();
            if (inventory == null) return false;

            if (spendsMoney) return inventory.TrySpendMoney(charge);

            string currency = CurrencyFor(NpcInterProvider?.Invoke());
            return inventory.RemoveItem(currency, charge);
        }

        /// <summary>
        /// <c>if(this.plata.id == "money" &amp;&amp; this.plata.kol &lt; _loc4_ &amp;&amp; Boolean(pip.vendor))
        /// pip.vendor.money += _loc4_ - this.plata.kol;</c> (<c>PipPageMed.as:442-445</c>) — the doctor
        /// keeps what the player paid.
        ///
        /// <para><b>Only for caps.</b> Gel and goods are the Auto-Doc's consumables, not its money, so an
        /// <c>adoc</c> or a <c>vdoc</c> banks nothing. The Oracle's test is on
        /// <c>this.plata.id</c>, not on the interaction kind, and the port tests the same thing.</para>
        /// </summary>
        private void CreditClinic(in PipMedPlan plan, int charge)
        {
            if (!plan.SpendsMoney || charge <= 0) return;

            VendorRuntime vendor = ClinicVendorProvider?.Invoke();
            if (vendor == null) return;

            vendor.Money += charge;
        }
    }
}
