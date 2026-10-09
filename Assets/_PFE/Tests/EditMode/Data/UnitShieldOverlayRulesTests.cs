using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Tests.Editor.Core;
using UnityEngine;

namespace PFE.Tests.EditMode.Data
{
    /// <summary>
    /// Pins the shield-dome selection to its AS3 oracle, and pins the two ways "no clip" can arise.
    ///
    /// <para><b>Why this fixture exists at all.</b> The renderer that consumes these answers
    /// (<c>UnitShieldOverlay</c>) cannot be exercised from an offline host — assigning a
    /// <c>SpriteRenderer</c> reaches a Unity <c>ECall</c> and the JIT refuses the method that mentions
    /// it. So the decision was split out into <see cref="UnitShieldOverlayRules"/>, which is a plain
    /// static class over plain data and can be called here. The frames are
    /// <c>new Sprite[n]</c> — an array of nulls — because <c>HasFrames</c> asks only for a length and
    /// constructing a real <c>Sprite</c> would be the very ECall this split avoids.</para>
    ///
    /// <para>The <i>definition</i> still has to be allocated, and it is a <c>ScriptableObject</c>, so it
    /// goes through <see cref="OfflineScriptableObject"/> — see the note on the
    /// <c>Definition</c> helper. <c>CharacterOverlayDefinition</c> is a plain <c>[Serializable]</c>
    /// class, so the clips are ordinary <c>new</c>.</para>
    ///
    /// <para>Oracle: <c>UnitAlicorn.as:191-203</c> (<c>tr == 3 ? visShit2 : visShit</c>) and
    /// <c>UnitBossAlicorn.as:130260</c> (<c>visShit3</c>).</para>
    /// </summary>
    [TestFixture]
    public class UnitShieldOverlayRulesTests
    {
        static CharacterOverlayDefinition Clip(int frameCount, string name = "clip")
        {
            return new CharacterOverlayDefinition
            {
                overlayName = name,
                frames = new Sprite[frameCount],
            };
        }

        static UnitShieldOverlayDefinition Definition(
            CharacterOverlayDefinition normal,
            CharacterOverlayDefinition large,
            CharacterOverlayDefinition boss)
        {
            // NOT `new UnitShieldOverlayDefinition { ... }`. The rules class is Unity-free, but the
            // *definition* it is handed is a ScriptableObject, and `new` runs UnityEngine.Object's
            // constructor, which is an ECall — measured: it threw for exactly the five tests that call
            // this helper, and only those. `OfflineScriptableObject.Create` uses the real allocator in
            // the editor and a constructor-less stub offline.
            var definition = OfflineScriptableObject.Create<UnitShieldOverlayDefinition>();
            definition.normal = normal;
            definition.large = large;
            definition.boss = boss;
            return definition;
        }

        // ── KindFor ─────────────────────────────────────────────────────────────

        [Test]
        public void KindFor_Tr3IsTheOnlyTierThatSwapsTheDome()
        {
            // UnitAlicorn.as:191 — `if(this.tr == 3) new visShit2() else new visShit()`.
            Assert.AreEqual(UnitShieldOverlayKind.Large, UnitShieldOverlayRules.KindFor(3, boss: false));
            Assert.AreEqual(UnitShieldOverlayKind.Normal, UnitShieldOverlayRules.KindFor(1, boss: false));
            Assert.AreEqual(UnitShieldOverlayKind.Normal, UnitShieldOverlayRules.KindFor(2, boss: false));

            // The family template id `alicorn` parses to tier 0 and the oracle never spawns it directly,
            // but it must not fall off the end of the ladder into `None` — it is still an alicorn and it
            // still gets the ordinary dome.
            Assert.AreEqual(UnitShieldOverlayKind.Normal, UnitShieldOverlayRules.KindFor(0, boss: false));
        }

        [Test]
        public void KindFor_BossOverridesTheTier()
        {
            // UnitBossAlicorn.as:130260 instantiates visShit3 regardless of `tr`.
            Assert.AreEqual(UnitShieldOverlayKind.Boss, UnitShieldOverlayRules.KindFor(3, boss: true));
            Assert.AreEqual(UnitShieldOverlayKind.Boss, UnitShieldOverlayRules.KindFor(1, boss: true));
        }

        // ── HasFrames ───────────────────────────────────────────────────────────

        [Test]
        public void HasFrames_RejectsNullAndEmpty()
        {
            Assert.IsFalse(UnitShieldOverlayRules.HasFrames(null), "a null clip is not available");

            // The one that bites: a field can be non-null with an empty array — an asset created before
            // the import, or an inspector edit that cleared the list. Treating that as available draws
            // sprite index 0 of a null array.
            Assert.IsFalse(UnitShieldOverlayRules.HasFrames(Clip(0)), "an empty frames array is not available");
            Assert.IsFalse(UnitShieldOverlayRules.HasFrames(
                new CharacterOverlayDefinition { frames = null }), "a null frames array is not available");

            Assert.IsTrue(UnitShieldOverlayRules.HasFrames(Clip(1)));
        }

        // ── Select ──────────────────────────────────────────────────────────────

        [Test]
        public void Select_ReturnsTheExactDomeWhenItHasFrames()
        {
            var normal = Clip(20, "shit");
            var large = Clip(20, "shit2");
            var def = Definition(normal, large, Clip(12, "shit3"));

            Assert.AreSame(large, UnitShieldOverlayRules.Select(def, tier: 3, boss: false));
            Assert.AreSame(normal, UnitShieldOverlayRules.Select(def, tier: 1, boss: false));
            Assert.AreSame(def.boss, UnitShieldOverlayRules.Select(def, tier: 3, boss: true));
        }

        [Test]
        public void Select_FallsBackToTheOrdinaryDomeWhenTheTierDomeIsMissing()
        {
            // visShit2 and visShit3 are exported but not imported into this project yet, so this is the
            // live case for a tr3 alicorn today. Drawing the ordinary dome is closer to the oracle than
            // drawing nothing; the caller reports the substitution.
            var normal = Clip(20, "shit");
            var def = Definition(normal, large: Clip(0), boss: Clip(0));

            Assert.AreSame(normal, UnitShieldOverlayRules.Select(def, tier: 3, boss: false));
            Assert.IsFalse(UnitShieldOverlayRules.IsExactMatch(def, tier: 3, boss: false),
                "the caller needs to know this was a stand-in");
        }

        [Test]
        public void Select_DoesNotFallBackToTheBossDome()
        {
            // The trap a "first non-empty clip" rule falls into: as soon as visShit3 alone were imported,
            // a tr3 alicorn would be handed the boss's 12-frame silhouette — a dome the oracle never
            // gives that unit. The fallback is `normal` by name, not "whatever exists".
            var def = Definition(normal: Clip(0), large: Clip(0), boss: Clip(12, "shit3"));

            Assert.IsNull(UnitShieldOverlayRules.Select(def, tier: 3, boss: false));
            Assert.IsNull(UnitShieldOverlayRules.Select(def, tier: 1, boss: false));
        }

        [Test]
        public void Select_WithNoDefinitionOrNoFramesAnywhere_AnswersNull()
        {
            Assert.IsNull(UnitShieldOverlayRules.Select(null, tier: 1, boss: false));

            var empty = Definition(Clip(0), Clip(0), Clip(0));
            Assert.IsNull(UnitShieldOverlayRules.Select(empty, tier: 1, boss: false));
            Assert.IsFalse(UnitShieldOverlayRules.IsExactMatch(empty, tier: 1, boss: false));
        }

        [Test]
        public void IsExactMatch_IsTrueOnlyWhenTheUnitsOwnDomeResolved()
        {
            var def = Definition(Clip(20), Clip(20), Clip(12));

            Assert.IsTrue(UnitShieldOverlayRules.IsExactMatch(def, tier: 3, boss: false));
            Assert.IsTrue(UnitShieldOverlayRules.IsExactMatch(def, tier: 1, boss: false));
            Assert.IsTrue(UnitShieldOverlayRules.IsExactMatch(def, tier: 1, boss: true));
        }
    }
}
