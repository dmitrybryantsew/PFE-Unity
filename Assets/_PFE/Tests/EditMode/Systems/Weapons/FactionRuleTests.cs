using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Entities.Player;
using PFE.Entities.Units;
using PFE.Systems.Weapons;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="FactionRule"/> — who may damage whom — against AS3's <c>fraction</c>.
    ///
    /// <para><b>Why this fixture exists.</b> The port had no friendly fire at all: no damage path
    /// compared factions, and <c>DamageContext.Owner</c> was <c>null</c> on every one of the six
    /// weapon controllers. Two existing tests claimed to cover friendly fire —
    /// <c>CombatLogicTests.Enemy_Explosion_HasReducedFriendlyFire</c> and
    /// <c>CombatSystemTests.Enemy_FriendlyFire_IsReduced</c> — and both compute the formula
    /// (<c>100 * 0.25</c>) <i>inside the test body</i> and assert the result. They pass with the
    /// combat system deleted. So this fixture asserts the rule's <b>outputs</b> instead, and every
    /// value here is one the rule has to earn.</para>
    ///
    /// <para><b>The oracle.</b> <c>fe/unit/Unit.as:78-86</c> (the constants), <c>:454</c> (the default),
    /// <c>fe/weapon/Bullet.as:515</c> (direct hit), <c>:764-786</c> and <c>:817-825</c> (the two
    /// explosion variants), <c>fe/weapon/PhisBullet.as:151</c> (thrown contact),
    /// <c>fe/unit/UnitPlayer.as:385</c> (the player's faction is set in code).</para>
    /// </summary>
    [TestFixture]
    public class FactionRuleTests
    {
        private const string UnitsPath = "Assets/_PFE/Data/Resources/Units";

        private const float Tol = 1e-6f;

        // ── 1. The enum has to decode AS3's numbers ──────────────────────────────

        /// <summary>
        /// The port's <see cref="FactionType"/> used to be numbered <c>Neutral=0, Player=1, Enemy=2,
        /// Unknown=3, Special=4</c> — which put <b>Player at 1</b>, the value AS3 uses for a
        /// <i>monster</i>. Every one of the 29 units the importer wrote as <c>fraction=1</c> therefore
        /// decoded as the player's own faction, i.e. the player would have been immune to a third of
        /// the bestiary. Asserting the numbers is the only way that stays fixed: the enum is cast
        /// straight from the XML attribute, so a cosmetic-looking renumbering is a behavioural change.
        /// </summary>
        [Test]
        public void FactionType_DecodesAs3Constants()
        {
            Assert.That((int)FactionType.Neutral, Is.EqualTo(0),   "Unit.as:454 `public var fraction:int = 0`");
            Assert.That((int)FactionType.Monster, Is.EqualTo(1),   "Unit.F_MONSTER");
            Assert.That((int)FactionType.Raider,  Is.EqualTo(2),   "Unit.F_RAIDER");
            Assert.That((int)FactionType.Zombie,  Is.EqualTo(3),   "Unit.F_ZOMBIE");
            Assert.That((int)FactionType.Robot,   Is.EqualTo(4),   "Unit.F_ROBOT");
            Assert.That((int)FactionType.Player,  Is.EqualTo(100), "Unit.F_PLAYER — 100, not 1");
        }

        /// <summary>
        /// A duplicate value would make two AS3 factions indistinguishable after the cast, which is a
        /// silent failure: the comparison still compiles and still returns a bool.
        /// </summary>
        [Test]
        public void FactionType_ValuesAreDistinct()
        {
            var values = new[]
            {
                FactionType.Neutral, FactionType.Monster, FactionType.Raider,
                FactionType.Zombie,  FactionType.Robot,   FactionType.Player,
            }.Select(f => (int)f).ToList();

            CollectionAssert.AllItemsAreUnique(values,
                "Two FactionType members share a numeric value, so a faction comparison cannot tell them apart.");
        }

        // ── 2. Direct hits (Bullet.as:515) ───────────────────────────────────────

        /// <summary>
        /// AS3 <c>Bullet.as:515</c>: <c>if((this.targetObj || _loc2_.fraction != this.owner.fraction)
        /// &amp;&amp; …)</c> — a unit is hit only when its faction <i>differs</i>. This is what stops a
        /// bullet hitting its own shooter, which is necessary because the muzzle is the unit's own
        /// centre (<c>Unit.as:3290-3291</c>).
        /// </summary>
        [Test]
        public void CanHitDirectly_SameFaction_IsBlocked()
        {
            Assert.That(FactionRule.CanHitDirectly(FactionType.Player, FactionType.Player), Is.False,
                "The player's own bullet must not hit the player: AS3 compares fraction, and the muzzle sits inside the body.");
            Assert.That(FactionRule.CanHitDirectly(FactionType.Raider, FactionType.Raider), Is.False,
                "Same rule for every faction, not just the player.");
            Assert.That(FactionRule.CanHitDirectly(FactionType.Neutral, FactionType.Neutral), Is.False,
                "An unowned projectile (Neutral) is still blocked from a Neutral unit — the comparison is equality, not 'is it the player'.");
        }

        [Test]
        public void CanHitDirectly_DifferentFaction_IsAllowed()
        {
            Assert.That(FactionRule.CanHitDirectly(FactionType.Player, FactionType.Raider), Is.True);
            Assert.That(FactionRule.CanHitDirectly(FactionType.Raider, FactionType.Player), Is.True);
            Assert.That(FactionRule.CanHitDirectly(FactionType.Monster, FactionType.Robot), Is.True,
                "Two non-player factions are not allies just because neither is the player.");
        }

        /// <summary>
        /// <b>The discrimination control.</b> Both of the tests above can be satisfied by a stub that
        /// returns a constant — one of them always passes. Asserting the two answers <i>in one
        /// place</i>, against the same attacker, is what makes the predicate falsifiable: no constant
        /// return value satisfies both lines.
        /// </summary>
        [Test]
        public void CanHitDirectly_IsNotAConstant()
        {
            bool sameSide = FactionRule.CanHitDirectly(FactionType.Raider, FactionType.Raider);
            bool otherSide = FactionRule.CanHitDirectly(FactionType.Raider, FactionType.Player);

            Assert.That(sameSide, Is.False, "constant-true implementation");
            Assert.That(otherSide, Is.True, "constant-false implementation");
            Assert.That(sameSide, Is.Not.EqualTo(otherSide),
                "The predicate must depend on its arguments; if these are equal, the rule is ignoring them.");
        }

        /// <summary>
        /// AS3's <c>targetObj</c> exemption: a bullet handed an explicit target hits it whatever the
        /// faction. The port does not store a guided target today (its homing re-picks every tick), so
        /// this is the one branch of the predicate with no production caller — asserted anyway, so the
        /// parameter cannot be quietly dropped when the port does grow one.
        /// </summary>
        [Test]
        public void CanHitDirectly_GuidedTarget_OverridesFaction()
        {
            Assert.That(
                FactionRule.CanHitDirectly(FactionType.Raider, FactionType.Raider, hasGuidedTarget: true),
                Is.True,
                "AS3: `(this.targetObj || fraction != owner.fraction)` — an explicit target bypasses the faction test.");
        }

        // ── 3. Explosions (Bullet.as:764-786) ────────────────────────────────────

        /// <summary>Same faction, neither is the player → quartered.</summary>
        [Test]
        public void Explosion_SameFaction_NotPlayer_IsQuartered()
        {
            Assert.That(
                FactionRule.ExplosionMultiplier(FactionType.Raider, FactionType.Raider, targetIsPlayer: false),
                Is.EqualTo(0.25f).Within(Tol),
                "AS3 `_loc5_ *= 0.25` when owner.fraction == target.fraction && target.fraction != F_PLAYER.");
            Assert.That(
                FactionRule.ExplosionMultiplier(FactionType.Monster, FactionType.Monster, targetIsPlayer: false),
                Is.EqualTo(0.25f).Within(Tol),
                "The reduction is per-faction, not player-specific.");
        }

        /// <summary>Different factions → unscaled, in both directions.</summary>
        [Test]
        public void Explosion_DifferentFaction_IsUnscaled()
        {
            Assert.That(
                FactionRule.ExplosionMultiplier(FactionType.Player, FactionType.Raider, targetIsPlayer: false),
                Is.EqualTo(1f).Within(Tol),
                "A player's explosion does full damage to a raider.");
            Assert.That(
                FactionRule.ExplosionMultiplier(FactionType.Raider, FactionType.Player, targetIsPlayer: true),
                Is.EqualTo(1f).Within(Tol),
                "An enemy's explosion does full damage to the player — there is no player damage reduction here.");
        }

        /// <summary>
        /// <b>The player is not immune to their own explosions, and this test is the guard against
        /// "fixing" that.</b> AS3 excludes <c>F_PLAYER</c> from the <c>×0.25</c> gate and gives it
        /// <c>pers.autoExpl</c> instead, whose default is <b>1</b> (<c>Pers.as:213</c>; the
        /// <c>autoExpl</c> skill at <c>AllData.as:5573</c> is what lowers it to 0.25). Full
        /// self-explosion damage is therefore the oracle's <i>default</i>, and a port that quartered it
        /// would be inventing a balance change.
        /// </summary>
        [Test]
        public void Explosion_PlayerOwnExplosion_IsFullDamageByDefault()
        {
            Assert.That(FactionRule.DefaultAutoExplosionMultiplier, Is.EqualTo(1f).Within(Tol),
                "Pers.autoExpl defaults to 1 — not to the 0.25 the skill applies.");
            Assert.That(
                FactionRule.ExplosionMultiplier(FactionType.Player, FactionType.Player, targetIsPlayer: true),
                Is.EqualTo(1f).Within(Tol),
                "Full self-explosion damage is correct by default. Do not 'fix' this to 0.25.");
        }

        /// <summary>
        /// The two gates are <b>sequential and independent</b>, not a product. Once the skill is wired,
        /// the player's own explosion is <c>autoExpl</c> (0.25) — and a non-player ally stays at the
        /// flat 0.25, because the second gate requires <c>owner.fraction == F_PLAYER</c>.
        ///
        /// <para>A product would give 0.0625 here. That is the failure this test names.</para>
        /// </summary>
        [Test]
        public void Explosion_AutoExplSkill_AppliesOnlyToThePlayersOwn()
        {
            const float skilledAutoExpl = 0.25f;   // AllData.as:5573 <sk id='autoExpl' v1='0.25'/>

            Assert.That(
                FactionRule.ExplosionMultiplier(FactionType.Player, FactionType.Player, true, skilledAutoExpl),
                Is.EqualTo(0.25f).Within(Tol),
                "With the skill, the player's own explosion is autoExpl — not autoExpl × 0.25 = 0.0625.");

            Assert.That(
                FactionRule.ExplosionMultiplier(FactionType.Raider, FactionType.Raider, false, skilledAutoExpl),
                Is.EqualTo(0.25f).Within(Tol),
                "The skill must not reach a non-player ally: the second gate needs owner == F_PLAYER. " +
                "A value of 0.0625 here means the two gates were multiplied together.");
        }

        /// <summary>
        /// AS3's asymmetry, asserted so it is not mistaken for a bug: the first gate excludes the
        /// player's faction entirely, so the player's explosion does <b>full</b> damage to a
        /// Player-faction ally, while a raider's explosion is quartered on another raider.
        /// </summary>
        [Test]
        public void Explosion_PlayerFactionAlly_TakesFullDamageFromThePlayer()
        {
            Assert.That(
                FactionRule.ExplosionMultiplier(FactionType.Player, FactionType.Player, targetIsPlayer: false),
                Is.EqualTo(1f).Within(Tol),
                "`target.fraction != F_PLAYER` fails for a Player-faction ally, so the ×0.25 gate is skipped. " +
                "That asymmetry is AS3's (Bullet.as:764), not an oversight in the port.");
        }

        // ── 4. The runtime faction the rule is fed ───────────────────────────────

        /// <summary>
        /// AS3 sets the player's faction in <b>code</b> — <c>UnitPlayer.as:385</c>
        /// <c>fraction = F_PLAYER</c> — because <c>littlepip</c> carries no <c>fraction</c> attribute in
        /// <c>AllData.as</c> at all. The port mirrors that with an override, so this test is what keeps
        /// the override from being deleted as "redundant with the data".
        /// </summary>
        [Test]
        public void PlayerController_IsPlayer_AndReportsPlayerFaction()
        {
            var go = new GameObject("faction-rule-test-player");
            try
            {
                var player = go.AddComponent<PlayerController>();

                Assert.That(player.IsPlayer, Is.True,
                    "UnitController.IsPlayer is `virtual => false` and nothing overrode it, so every unit " +
                    "reported false — including the player. AS3 branches on `_loc1_.player` at Bullet.as:782/817, " +
                    "which is exactly where the player's own-explosion multiplier lives.");

                Assert.That(player.Faction, Is.EqualTo(FactionType.Player),
                    "UnitPlayer.as:385 sets fraction = F_PLAYER in code; littlepip has no fraction attribute.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// The base fallback: a unit with no <c>UnitDefinition</c> assigned is <see cref="FactionType.Neutral"/>,
        /// which is AS3's own default (<c>Unit.as:454</c>). Neutral means "hits everyone", so an
        /// unconfigured unit must not silently acquire a side.
        /// </summary>
        [Test]
        public void UnitController_WithNoStats_FallsBackToNeutral()
        {
            var go = new GameObject("faction-rule-test-unit");
            try
            {
                var unit = go.AddComponent<UnitController>();

                Assert.That(unit.Faction, Is.EqualTo(FactionType.Neutral),
                    "A unit with no UnitDefinition must not acquire a faction — Neutral is AS3's default " +
                    "and means it collides with everyone, which is the pre-existing behaviour.");
                Assert.That(unit.IsPlayer, Is.False,
                    "Only the player's controller reports true.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // ── 5. The imported-content contract ─────────────────────────────────────

        private static List<UnitDefinition> LoadAllUnitDefs()
        {
            var defs = new List<UnitDefinition>();
            foreach (string guid in AssetDatabase.FindAssets("t:UnitDefinition", new[] { UnitsPath }))
            {
                var def = AssetDatabase.LoadAssetAtPath<UnitDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (def != null) defs.Add(def);
            }
            return defs;
        }

        /// <summary>
        /// The baked assets must carry more than one faction, and every value must be one AS3
        /// declares. This is the data half of the same defect: the importer wrote the <c>fraction</c>
        /// attribute straight into a <see cref="FactionType"/> cast, so a numbering mistake or a
        /// regex that cannot match the attribute shows up here rather than in a play-test.
        ///
        /// <para><b>Known gap, deliberately not asserted as red.</b> These assets were baked
        /// <i>before</i> <c>UnitDataImporter</c> learned to resolve <c>parent=</c> inheritance and
        /// before its regex accepted more than one digit. Measured on 2026-09-27: 88 assets carry
        /// <c>2</c>, 30 carry <c>4</c>, 29 carry <c>1</c>, 1 carries <c>0</c> — and <b>zero</b> carry
        /// <c>100</c>, because <c>fraction='100'</c> (npc, vendor, doctor, captive, ponpon) could not
        /// match a single-digit pattern and fell back to the C# default. The strict form of this test
        /// is therefore "exactly five assets carry <see cref="FactionType.Player"/>"; it will be
        /// asserted once the units are re-imported. Until then the invariant below is what holds, and
        /// the player-versus-everyone case is unaffected because the player's faction comes from the
        /// override, not from data.</para>
        /// </summary>
        [Test]
        public void ImportedContent_CarriesMoreThanOneDeclaredFaction()
        {
            var defs = LoadAllUnitDefs();

            Assert.That(defs.Count, Is.GreaterThan(0),
                $"No UnitDefinition assets found under '{UnitsPath}' — this test would otherwise pass vacuously.");

            var declared = new HashSet<int>
            {
                (int)FactionType.Neutral, (int)FactionType.Monster, (int)FactionType.Raider,
                (int)FactionType.Zombie,  (int)FactionType.Robot,   (int)FactionType.Player,
            };

            var undeclared = defs
                .Where(d => !declared.Contains((int)d.fraction))
                .Select(d => $"{d.id}={(int)d.fraction}")
                .ToList();

            CollectionAssert.IsEmpty(undeclared,
                "A baked unit carries a faction value AS3 does not declare. Re-run PFE/Data/Import Units from AllData.as.");

            var distinct = defs.Select(d => (int)d.fraction).Distinct().ToList();

            Assert.That(distinct.Count, Is.GreaterThanOrEqualTo(3),
                $"Only {distinct.Count} distinct faction(s) across {defs.Count} unit assets " +
                $"({string.Join(", ", distinct.Select(v => v.ToString()).ToArray())}). The importer's " +
                "failure mode is collapsing every unit onto the C# field default, which reads as a " +
                "working import. Re-run PFE/Data/Import Units from AllData.as.");

            Assert.That(distinct, Does.Contain((int)FactionType.Monster),
                "No unit is a Monster (1). The 29 AS3 units with fraction='1' are the ones that would " +
                "decode as the player's faction if FactionType.Player were numbered 1 again.");
            Assert.That(distinct, Does.Contain((int)FactionType.Robot),
                "No unit is a Robot (4). Its absence means the importer stopped reading the attribute.");
        }
    }
}
