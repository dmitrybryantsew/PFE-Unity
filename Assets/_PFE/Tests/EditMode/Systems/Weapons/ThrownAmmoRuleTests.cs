using NUnit.Framework;
using PFE.Systems.Weapons;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="ThrownAmmoRule"/> — AS3 <c>WThrow.getAmmo()</c> (<c>WThrow.as:268-280</c>) and
    /// the <c>ammo = id</c> override at <c>:42</c>.
    ///
    /// <para><b>The regression this exists for.</b> The port ran <c>WThrow</c>'s <b>NPC</b> counter for
    /// the player. <c>kolAmmo</c> starts at 4 and nothing ever refills it (<c>WThrow.reloadWeapon()</c>
    /// is an empty override, <c>:264</c>), so a player threw four grenades and then could not throw
    /// again for the rest of the session — reported as <i>"quickly press fire, it throws like 5 and
    /// then i cant throw grenades again"</i>. The assertion that matters is
    /// <see cref="Decide_NoInventory_TheCounterCannotGateIt"/>: the counter must be unable to refuse a
    /// throw when no inventory is wired, whatever value it holds.</para>
    /// </summary>
    [TestFixture]
    public class ThrownAmmoRuleTests
    {
        // ── AmmoIdFor: WThrow.as:42, `ammo = id` ──────────────────────────────

        [Test]
        public void AmmoIdFor_EmptyResolvedAmmoType_FallsBackToTheWeaponId()
        {
            // Every throwable's imported `ammoType` is empty — the <weapon> row carries no `ammo`
            // attribute and WThrow overwrites it in the constructor anyway. Without this fallback the
            // controller would ask the inventory for a nameless item and refuse every throw.
            Assert.AreEqual("fgren", ThrownAmmoRule.AmmoIdFor(string.Empty, "fgren"));
        }

        [Test]
        public void AmmoIdFor_NullResolvedAmmoType_FallsBackToTheWeaponId()
        {
            Assert.AreEqual("molotov", ThrownAmmoRule.AmmoIdFor(null, "molotov"));
        }

        [Test]
        public void AmmoIdFor_ResolvedOverride_Wins()
        {
            // The debug ammo swap must reach the count as well as the ballistics lookup, or the reload
            // and the guard would read two different buckets.
            Assert.AreEqual("batt", ThrownAmmoRule.AmmoIdFor("batt", "lasp"));
        }

        // ── Decide: the two halves of getAmmo() ───────────────────────────────

        [Test]
        public void Decide_InventoryWithRounds_ConsumesOneItem()
        {
            Assert.AreEqual(ThrownAmmoRule.Outcome.ConsumeInventory,
                ThrownAmmoRule.Decide(hasAmmoSource: true, inventoryRounds: 3, kolAmmo: 4));
        }

        [Test]
        public void Decide_InventoryEmpty_Refuses()
        {
            // AS3's getInvAmmo returns -1 when `items[ammo].kol < param3`, so `> 0` is false.
            Assert.AreEqual(ThrownAmmoRule.Outcome.RefuseInventoryEmpty,
                ThrownAmmoRule.Decide(hasAmmoSource: true, inventoryRounds: 0, kolAmmo: 4));
        }

        [Test]
        public void Decide_NoInventory_IsTrainingMode()
        {
            // The port's contract for a null IAmmoSource, as IAmmoSource and RangedWeaponController
            // both document it: null is the training / no-inventory case, so the throw is free.
            //
            // NOTE (2026-10-05): a live session is no longer in this state. PlayerController now builds
            // a PlayerInventory, which assigns PlayerWeaponLoadout.AmmoSource, so the player takes the
            // INVENTORY branch — and an empty inventory refuses the throw rather than throwing free.
            // This case remains the correct contract for a null source; it is simply no longer the
            // branch a live player hits. The fixture below pins the contract, not the live path.
            Assert.AreEqual(ThrownAmmoRule.Outcome.TrainingInfinite,
                ThrownAmmoRule.Decide(hasAmmoSource: false, inventoryRounds: 0, kolAmmo: 4));
        }

        [Test]
        public void Decide_NoInventory_TheCounterCannotGateIt()
        {
            // The defect, stated as a property: with no inventory wired, the NPC counter must not be
            // able to turn a throw into a refusal. Before the fix the port read `kolAmmo <= 0` first,
            // so the fifth throw of a session was refused forever.
            for (int kolAmmo = 0; kolAmmo <= 8; kolAmmo++)
            {
                Assert.AreEqual(ThrownAmmoRule.Outcome.TrainingInfinite,
                    ThrownAmmoRule.Decide(hasAmmoSource: false, inventoryRounds: 0, kolAmmo: kolAmmo),
                    $"kolAmmo={kolAmmo} must not gate a throw when no inventory is wired");
            }
        }

        [Test]
        public void Decide_NoInventoryAndEmptyInventory_AreDifferentOutcomes()
        {
            // The control for the pair above. "No inventory" and "an empty inventory" are genuinely
            // different — collapsing them into one value is what made the original defect silent
            // (a loader degrading to an empty set, lesson #69).
            Assert.AreNotEqual(
                ThrownAmmoRule.Decide(hasAmmoSource: false, inventoryRounds: 0, kolAmmo: 4),
                ThrownAmmoRule.Decide(hasAmmoSource: true,  inventoryRounds: 0, kolAmmo: 4));
        }

        [Test]
        public void Decide_NoInventory_IgnoresTheInventoryRoundCount()
        {
            // A non-zero count with no source must still be training mode: the flag decides, not the
            // count, so a caller that passes a stale count cannot silently enable inventory mode.
            Assert.AreEqual(ThrownAmmoRule.Outcome.TrainingInfinite,
                ThrownAmmoRule.Decide(hasAmmoSource: false, inventoryRounds: 99, kolAmmo: 0));
        }

        // ── DecideForCounterOwner: AS3's non-player branch ────────────────────

        [Test]
        public void DecideForCounterOwner_WithRounds_ConsumesTheCounter()
        {
            Assert.AreEqual(ThrownAmmoRule.Outcome.ConsumeCounter, ThrownAmmoRule.DecideForCounterOwner(1));
        }

        [Test]
        public void DecideForCounterOwner_AtZero_Refuses()
        {
            // AS3: `if(this.kolAmmo <= 0) return false;` — the bound is inclusive.
            Assert.AreEqual(ThrownAmmoRule.Outcome.RefuseCounterEmpty, ThrownAmmoRule.DecideForCounterOwner(0));
        }

        [Test]
        public void DecideForCounterOwner_BelowZero_Refuses()
        {
            Assert.AreEqual(ThrownAmmoRule.Outcome.RefuseCounterEmpty, ThrownAmmoRule.DecideForCounterOwner(-3));
        }

        [Test]
        public void UsesInventory_IsThePortStandInForOwnerPlayer()
        {
            // AS3's test is `owner.player`; the port has no per-owner flag, and a wired IAmmoSource is
            // the meaning that interface already carries. Pinned so a future change to the proxy is a
            // deliberate edit rather than a silent one.
            Assert.IsTrue(ThrownAmmoRule.UsesInventory(hasAmmoSource: true));
            Assert.IsFalse(ThrownAmmoRule.UsesInventory(hasAmmoSource: false));
        }
    }
}
