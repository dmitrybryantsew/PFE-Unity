using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Weapons;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins that <see cref="DamageContext.FromWeapon"/> carries the weapon's own <c>&lt;dop&gt;</c>
    /// node onto the shot.
    ///
    /// <para><b>Why this fixture exists.</b> The factory read every other fire-time term off the
    /// <see cref="WeaponDefinition"/> and passed three of them as literals —
    /// <c>dopEffect: null, dopDamage: 0f, dopChance: 1f</c>. Nothing else in the port ever populated
    /// <c>DopEffect</c>, so <c>OnHitEffectProducers.ApplyWeaponDop</c> hit its
    /// <c>if (string.IsNullOrEmpty(dopEffect)) return;</c> guard on every hit and no weapon's
    /// status effect ever fired: <c>igni</c> (flamer), <c>ice</c>, <c>blind</c>, <c>acid</c>,
    /// <c>pink</c>. The player's report was "used flamer on dummy — no particles", and it was
    /// indistinguishable from "the flamer simply does not set anything on fire", because a missing
    /// status effect is not a crash and not a warning.</para>
    ///
    /// <para><b>AS3 authority.</b> <c>Weapon.as:693-697</c> — <c>this.dopEffect = param1.@effect;
    /// this.dopDamage = param1.@damage;</c> read straight off the weapon's own node. The importer's
    /// half (<c>WeaponDataImporter.cs:451-453</c>) was already correct and is pinned by
    /// <c>WeaponXmlAttrsTests</c>; this fixture pins the half that was not — the definition reaching
    /// the shot.</para>
    ///
    /// <para><b>Unity-only.</b> <see cref="WeaponDefinition"/> is a <c>ScriptableObject</c>, so every
    /// test here needs the editor: offline they fail with the lesson-#43 ECall
    /// <c>SecurityException</c>, exactly like <c>MeleeOwnerStatTests</c>. They are a known, benign
    /// addition to the offline wall's <c>failed</c> count.</para>
    ///
    /// <para>Each unit test is paired: a positive case that sets the three fields, and a control whose
    /// definition carries no <c>&lt;dop&gt;</c> at all, so a factory that hardcoded <i>any</i>
    /// constant would fail one of the two.</para>
    /// </summary>
    [TestFixture]
    public class DamageContextFromWeaponTests
    {
        private const string WeaponsPath = "Assets/_PFE/Data/Resources/Weapons";

        private readonly List<Object> _spawned = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _spawned) Object.DestroyImmediate(o);
            _spawned.Clear();
        }

        private WeaponDefinition MakeDef(string effect, float damage, float chance)
        {
            var def = ScriptableObject.CreateInstance<WeaponDefinition>();
            def.weaponId   = "test_dop";
            def.dopEffect  = effect;
            def.dopDamage  = damage;
            def.dopChance  = chance;
            _spawned.Add(def);
            return def;
        }

        // ── The regression guard ─────────────────────────────────────────────────

        [Test]
        public void FromWeapon_CarriesTheWeaponsDopOntoTheShot()
        {
            // The flamer's own numbers (AllData.as:3147: `<dop effect='igni' damage='4' ch='0.2'/>`),
            // so all three literals the factory used to pass are wrong in a *distinguishable* way:
            // null vs "igni", 0 vs 4, 1 vs 0.2. A single hardcoded constant cannot satisfy this.
            var def = MakeDef("igni", 4f, 0.2f);

            DamageContext ctx = DamageContext.FromWeapon(def, null);

            Assert.AreEqual("igni", ctx.DopEffect,
                "the weapon's own <dop effect> must reach the shot (Weapon.as:693-697). An empty " +
                "effect makes OnHitEffectProducers.ApplyWeaponDop early-return, so no weapon in the " +
                "game ever applies a status effect.");
            Assert.AreEqual(4f, ctx.DopDamage, 0.0001f,
                "<dop damage> is the payload amount; 0 means the effect applies for nothing.");
            Assert.AreEqual(0.2f, ctx.DopChance, 0.0001f,
                "<dop ch> is the application chance; a hardcoded 1 would make every hit proc.");
        }

        [Test]
        public void FromWeapon_WithNoDopNode_CarriesAnEmptyEffect_SoTheProducerSkipsIt()
        {
            // Control. A weapon with no `<dop>` node must NOT acquire one — this is the half that
            // keeps a "carry the fields" fix from turning into "always apply something".
            var def = MakeDef(null, 0f, 1f);

            DamageContext ctx = DamageContext.FromWeapon(def, null);

            Assert.IsNull(ctx.DopEffect,
                "a weapon with no <dop> node must carry no effect, so ApplyWeaponDop skips it.");
            Assert.AreEqual(0f, ctx.DopDamage, 0.0001f, "no node, no payload.");
        }

        [Test]
        public void FromWeapon_CarriesASubCertainChance_SoTheProcIsActuallyRolled()
        {
            // Pairs the guard above: the flamer's 0.2 is what makes `dopChance < 1f && !chancePassed`
            // reachable. A factory that clamped or defaulted the chance to 1 would make every flamer
            // hit ignite, which looks like a working feature and is the wrong one.
            var def = MakeDef("igni", 4f, 0.2f);

            DamageContext ctx = DamageContext.FromWeapon(def, null);

            Assert.Less(ctx.DopChance, 1f,
                "0.2 must survive as a rolled chance, not be normalised to certainty.");
            Assert.Greater(ctx.DopChance, 0f,
                "…and it must not be zeroed either, which would make the proc unreachable.");
        }

        [Test]
        public void FromWeapon_LeavesTheChanceAtItsDefinitionDefault_WhenTheAttributeIsAbsent()
        {
            // WeaponDefinition.dopChance defaults to 1f (an absent `ch` attribute is CERTAIN, not 0 —
            // see the field's own doc). The old hardcoded 1f happened to match this case, which is
            // exactly why the defect hid: the *only* case anyone would notice was the one the
            // literal got right.
            var def = ScriptableObject.CreateInstance<WeaponDefinition>();
            def.weaponId = "test_no_ch";
            def.dopEffect = "stun";
            _spawned.Add(def);

            DamageContext ctx = DamageContext.FromWeapon(def, null);

            Assert.AreEqual(1f, ctx.DopChance, 0.0001f,
                "an absent <dop ch> is certain (Unit.as:3771: `dopCh >= 1 || Math.random() < dopCh`).");
            Assert.AreEqual("stun", ctx.DopEffect,
                "control: the effect still rides even though the chance was left at its default.");
        }

        // ── The content contract ─────────────────────────────────────────────────

        [Test]
        public void TheRealFlamerAsset_CarriesItsDopIntoTheShot()
        {
            // The end-to-end half: reads the actual imported asset, so this fails if either the
            // importer stops reading <dop> or the factory stops carrying it. Mirrors the last test in
            // WeaponDispatchTests — a guard in only one assembly cannot see the other half.
            var def = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(
                WeaponsPath + "/flamer.asset");

            Assert.IsNotNull(def, "flamer.asset must exist at " + WeaponsPath + ".");
            Assert.AreEqual("flamer", def.weaponId, "control: this is the weapon under test.");

            DamageContext ctx = DamageContext.FromWeapon(def, null);

            Assert.AreEqual("igni", ctx.DopEffect,
                "AllData.as:3147 gives the flamer `<dop effect='igni'/>`; the shot must carry it.");
            Assert.AreEqual(4f, ctx.DopDamage, 0.0001f, "…with damage='4'.");
            Assert.AreEqual(0.2f, ctx.DopChance, 0.0001f,
                "…and ch='0.2', so one hit in five ignites — the value the player was watching for.");
        }

        [Test]
        public void AtLeastOneWeaponInTheGameActuallyCarriesADopEffect()
        {
            // A guard on the *data* as a whole. If a future import run dropped the <dop> attribute
            // everywhere, every per-weapon test would still pass as long as one asset kept it — this
            // asserts the population is non-empty and reports how large it is.
            int withDop = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:WeaponDefinition", new[] { WeaponsPath }))
            {
                var def = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (def != null && !string.IsNullOrEmpty(def.dopEffect)) withDop++;
            }

            Assert.Greater(withDop, 0,
                "no imported weapon carries a <dop> effect — the importer or the data has regressed.");
            Assert.Greater(withDop, 5,
                "expected a meaningful population of dop weapons (igni/ice/blind/acid/pink); got " +
                withDop + ", which suggests the attribute stopped being read.");
        }
    }
}
