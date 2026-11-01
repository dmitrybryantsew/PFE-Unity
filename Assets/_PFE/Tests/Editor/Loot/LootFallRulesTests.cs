using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PFE.Systems.Loot;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Loot
{
    /// <summary>
    /// The falling half of <c>fe.loc.Loot</c> — <see cref="LootFallRules"/>.
    ///
    /// <para><b>Every expected value here was written from the oracle's line, not from the port's
    /// output.</b> That distinction is the whole point: the port had no falling-loot model at all, so
    /// there was no behaviour to observe and no regression to preserve — the only way this can be wrong
    /// is by disagreeing with <c>Loot.as</c>, and a fixture that records what the code does would
    /// record the disagreement too.</para>
    /// </summary>
    [TestFixture]
    public class LootFallRulesTests
    {
        // ── Gravity ────────────────────────────────────────────────────────────

        [Test]
        public void StepVelocity_AddsOnePixelPerFrame_AndStopsAtTheTerminal()
        {
            // Loot.as:352 — `if(!levit && !this.vsos && dy < World.maxdy) dy += World.ddy`.
            // Y-up here, so "falling faster" is a more negative vy.
            Assert.AreEqual(-1f, LootFallRules.StepVelocity(0f), "one frame of ddy from rest.");
            Assert.AreEqual(-2f, LootFallRules.StepVelocity(-1f));
            Assert.AreEqual(-20f, LootFallRules.StepVelocity(-19f), "19 -> 20 is the last acceleration.");

            Assert.AreEqual(-20f, LootFallRules.StepVelocity(-20f),
                "at the terminal the gate `dy < maxdy` is false, so nothing is added.");

            Assert.AreEqual(-25f, LootFallRules.StepVelocity(-25f),
                "and an already-faster speed is NOT pulled back to 20 — the oracle's clamp is a gate, " +
                "not a Math.Min.");
        }

        [Test]
        public void SubstepCount_IsFloorOfLongestOverMaxDeltaPlusOne()
        {
            // Loot.as:372 — `_loc1_ = Math.floor(Math.max(Math.abs(dx), Math.abs(dy)) / World.maxdelta) + 1`
            Assert.AreEqual(1, LootFallRules.SubstepCount(0f, 0f), "a motionless loot still asks for one step.");
            Assert.AreEqual(1, LootFallRules.SubstepCount(8.9f, 0f));
            Assert.AreEqual(2, LootFallRules.SubstepCount(9f, 0f), "exactly maxdelta needs a second step.");
            Assert.AreEqual(2, LootFallRules.SubstepCount(0f, -17.9f));
            Assert.AreEqual(3, LootFallRules.SubstepCount(0f, -20f), "floor(20/9) + 1 = 3.");
            Assert.AreEqual(2, LootFallRules.SubstepCount(-5f, 9f), "the LONGEST axis decides.");
        }

        // ── Landing ────────────────────────────────────────────────────────────

        [Test]
        public void LandingSound_FiresAboveFivePixelsPerFrame_AndNotAtFive()
        {
            // Loot.as:481 — `if(dy > 5 && Boolean(this.sndFall))`. Strict, so a 5 px/frame settle is silent.
            Assert.IsFalse(LootFallRules.ShouldPlayLandingSound(5f), "5 is not > 5.");
            Assert.IsFalse(LootFallRules.ShouldPlayLandingSound(0f));
            Assert.IsTrue(LootFallRules.ShouldPlayLandingSound(5.1f));
            Assert.IsTrue(LootFallRules.ShouldPlayLandingSound(20f));
        }

        [Test]
        public void LandingVolume_IsSpeedOverFifteen_AndWaterIsOverTen()
        {
            // Loot.as:483 — `Snd.ps(sndFall, X, Y, 0, dy / 15)`
            Assert.AreEqual(1f, LootFallRules.LandingVolume(15f), 1e-5f);
            Assert.AreEqual(20f / 15f, LootFallRules.LandingVolume(20f), 1e-5f);

            // Loot.as:547 — `Snd.ps("fall_item_water", X, Y, 0, dy / 10)`. A different divisor on
            // purpose: a splash at dy/15 would be quieter than the same impact on a floor.
            Assert.AreEqual(1f, LootFallRules.WaterVolume(10f), 1e-5f);
            Assert.AreNotEqual(LootFallRules.LandingVolume(10f), LootFallRules.WaterVolume(10f),
                "the two divisors must not collapse into one.");
        }

        // ── The throw ──────────────────────────────────────────────────────────

        [Test]
        public void JumpVelocity_ThrowsUpAndSideways_NeverDown()
        {
            // Loot.as:229-233 — `dx = Math.random() * 10 - 5; dy = Math.random() * 5 - 10;`
            // In AS3 dy is y-DOWN, so [-10,-5) is UPWARD. The port's vy is y-up, so the throw is +5..10.

            LootFallRules.JumpVelocity(0f, 0f, out float vxLow, out float vyHigh);
            Assert.AreEqual(-5f, vxLow, 1e-5f, "r=0 gives the leftmost shove.");
            Assert.AreEqual(10f, vyHigh, 1e-5f, "r=0 gives the strongest lob.");

            LootFallRules.JumpVelocity(1f, 1f, out float vxHigh, out float vyLow);
            Assert.AreEqual(5f, vxHigh, 1e-5f, "r->1 gives the rightmost shove.");
            Assert.AreEqual(5f, vyLow, 1e-5f, "r->1 gives the weakest lob, still upward.");

            // The direction is the part a sign slip gets wrong, and it still "works" when it is wrong —
            // the loot is simply buried instead of thrown. Pin it over the whole range.
            for (int i = 0; i <= 10; i++)
            {
                LootFallRules.JumpVelocity(i / 10f, i / 10f, out _, out float vy);
                Assert.Greater(vy, 0f, $"r={i / 10f}: a jumping loot must go UP.");
                Assert.LessOrEqual(vy, 10f);
                Assert.GreaterOrEqual(vy, 5f);
            }
        }

        // ── The sndFall ladder ─────────────────────────────────────────────────

        [Test]
        public void FallSound_DefaultsToFallItem()
        {
            Assert.AreEqual("fall_item",
                LootFallRules.ResolveFallSoundId(false, false, false, false, false, null, null));
        }

        [Test]
        public void FallSound_APlainItemUsesItsOwnRootFall()
        {
            Assert.AreEqual("fall_metal_small",
                LootFallRules.ResolveFallSoundId(false, false, false, false, false,
                                                 rootFall: "fall_metal_small", sndFall: null));
        }

        [Test]
        public void FallSound_SchemeAndBookArePaper_UnlessTheRowSaysOtherwise()
        {
            // Loot.as:206-215 — `if(tip == L_SCHEME) sndFall = "fall_paper"; if(tip == L_BOOK) ...`
            Assert.AreEqual("fall_paper",
                LootFallRules.ResolveFallSoundId(false, false, false, isScheme: true, isBook: false, null, null));
            Assert.AreEqual("fall_paper",
                LootFallRules.ResolveFallSoundId(false, false, false, isScheme: false, isBook: true, null, null));

            // ...and then `:216` lets the row's own @fall override the paper default.
            Assert.AreEqual("fall_caps",
                LootFallRules.ResolveFallSoundId(false, false, false, isScheme: true, isBook: false,
                                                 rootFall: "fall_caps", sndFall: null),
                "a scheme with an authored @fall is not paper.");
        }

        [Test]
        public void FallSound_ExplosiveAndAmmoUseTheirRootFall()
        {
            // Loot.as:134-137 (expl) and :158-161 (ammo) both read `item.xml.@fall`.
            Assert.AreEqual("fall_grenade",
                LootFallRules.ResolveFallSoundId(false, isExpl: true, isAmmo: false, false, false,
                                                 rootFall: "fall_grenade", sndFall: null));
            Assert.AreEqual("fall_metal_small",
                LootFallRules.ResolveFallSoundId(false, isExpl: false, isAmmo: true, false, false,
                                                 rootFall: "fall_metal_small", sndFall: null));
        }

        [Test]
        public void FallSound_AWeaponReadsTheSndChild_NotTheRootAttribute()
        {
            // Loot.as:120 — `if(this.item.xml.snd.@fall.length())`. The ONLY branch that reads the
            // <snd> child. This is the trap: three of the four branches read `@fall` on the row.
            Assert.AreEqual("fall_metal_small",
                LootFallRules.ResolveFallSoundId(isWeapon: true, false, false, false, false,
                                                 rootFall: null, sndFall: "fall_metal_small"));

            Assert.AreEqual("fall_item",
                LootFallRules.ResolveFallSoundId(isWeapon: true, false, false, false, false,
                                                 rootFall: "fall_gem", sndFall: null),
                "a weapon with only a root @fall keeps fall_item — the oracle never reads it there.");

            Assert.AreEqual("fall_metal_small",
                LootFallRules.ResolveFallSoundId(isWeapon: true, false, false, false, false,
                                                 rootFall: "fall_gem", sndFall: "fall_metal_small"),
                "the weapon branch reads ONLY snd/@fall, so a root @fall beside it changes nothing.");
        }

        [Test]
        public void FallSound_WeaponBranchIgnoresTheSchemeAndBookDefaults()
        {
            // A weapon is never paper, even if a caller mislabels it: the branches are exclusive in the
            // oracle's if/else-if chain, and `scheme`/`book` only run in the catch-all arm.
            Assert.AreEqual("fall_item",
                LootFallRules.ResolveFallSoundId(isWeapon: true, false, false, isScheme: true, isBook: true,
                                                 rootFall: null, sndFall: null));
        }

        [Test]
        public void FallSound_AnEmptyAttributeIsNotAValue()
        {
            Assert.AreEqual("fall_item",
                LootFallRules.ResolveFallSoundId(false, false, false, false, false,
                                                 rootFall: string.Empty, sndFall: string.Empty),
                "`@fall=''` behaves as absent — the oracle's guard is `.length()`.");
        }
    }

    /// <summary>
    /// The prop-side sound attributes — <see cref="MapObjectSoundRule"/>. AS3 <c>Box.as:238-252</c> parses
    /// them and <c>:508-532</c> / <c>:850-853</c> play them; the port read none of them.
    /// </summary>
    [TestFixture]
    public class MapObjectSoundRuleTests
    {
        private static List<MapObjectAttributeData> Attrs(params (string key, string value)[] pairs)
        {
            var list = new List<MapObjectAttributeData>();
            foreach (var (key, value) in pairs)
            {
                list.Add(new MapObjectAttributeData { key = key, value = value });
            }
            return list;
        }

        [Test]
        public void OpenSound_ReadsTheAuthoredOpenAttribute()
        {
            // The shape `chest.asset` actually ships: `open: metal_open`.
            Assert.AreEqual("metal_open", MapObjectSoundRule.OpenSound(Attrs(("cont", "chest"), ("open", "metal_open"))));
        }

        [Test]
        public void OpenSound_IsNullWhenAbsentOrEmpty()
        {
            Assert.IsNull(MapObjectSoundRule.OpenSound(Attrs(("cont", "chest"))));
            Assert.IsNull(MapObjectSoundRule.OpenSound(Attrs(("open", ""))),
                "an authored-but-empty @open leaves Box.sndOpen at \"\", which the guard rejects.");
            Assert.IsNull(MapObjectSoundRule.OpenSound(null));
        }

        [Test]
        public void OpenSound_IsCaseInsensitiveLikeEveryOtherAttributeRead()
        {
            // Delegated to MapObjectDataUtility.GetAttribute rather than a second loop, so it inherits
            // the ordinal-ignore-case rule every other map-object reader uses.
            Assert.AreEqual("metal_open", MapObjectSoundRule.OpenSound(Attrs(("Open", "metal_open"))));
        }

        [Test]
        public void CloseAndDieAreSeparateAttributes()
        {
            var attrs = Attrs(("open", "metal_open"), ("close", "big_door_close"), ("die", "wood_break"),
                              ("fall", "fall_metal_big"));

            Assert.AreEqual("metal_open", MapObjectSoundRule.OpenSound(attrs));
            Assert.AreEqual("big_door_close", MapObjectSoundRule.CloseSound(attrs));
            Assert.AreEqual("wood_break", MapObjectSoundRule.DieSound(attrs));
            Assert.AreEqual("fall_metal_big", MapObjectSoundRule.FallSound(attrs));
        }

        [Test]
        public void PlaysOnOpen_IsGatedOnTestLoot_ButCloseIsNot()
        {
            // Box.as:510 — `(param1 == "open" || param1 == "comein") && sndOpen != "" && !World.w.testLoot`
            Assert.IsTrue(MapObjectSoundRule.PlaysOnOpen("metal_open", testLoot: false));
            Assert.IsFalse(MapObjectSoundRule.PlaysOnOpen("metal_open", testLoot: true),
                "a loot-test sweep silences the crates it opens.");
            Assert.IsFalse(MapObjectSoundRule.PlaysOnOpen(null, testLoot: false));
            Assert.IsFalse(MapObjectSoundRule.PlaysOnOpen(string.Empty, testLoot: false));

            // Box.as:514 — `if(param1 == "close" && this.sndClose != "")` — NO testLoot term.
            Assert.IsTrue(MapObjectSoundRule.PlaysOnClose("big_door_close"));
            Assert.IsFalse(MapObjectSoundRule.PlaysOnClose(null));

            // The asymmetry is the oracle's, and it is pinned by the SIGNATURES: PlaysOnClose has no
            // testLoot parameter at all, so a later "cleanup" cannot quietly make them symmetric without
            // changing a call site.
        }

        [Test]
        public void ShippedChestDefinition_CarriesASoundThatTheCatalogCanResolve()
        {
            // The end-to-end claim behind this whole change: the id was already in the data AND already
            // in the audio catalog, so the silence was a wiring gap and not a content gap. Asserted on
            // the shipped files rather than on a hand-built list, because a hand-built list would pass
            // even if every asset on disk had lost the attribute.
            string root = FindProjectRoot();
            Assert.IsNotNull(root, "could not locate the project root");

            string chestPath = Path.Combine(root, "Assets/_PFE/Data/Resources/MapObjects/Definitions/chest.asset");
            Assert.IsTrue(File.Exists(chestPath), $"missing {chestPath}");

            string chestText = File.ReadAllText(chestPath);
            int openIndex = chestText.IndexOf("- key: open", System.StringComparison.Ordinal);
            Assert.Greater(openIndex, -1, "chest.asset no longer carries an `open` attribute");

            int valueIndex = chestText.IndexOf("value:", openIndex, System.StringComparison.Ordinal);
            Assert.Greater(valueIndex, -1);
            string line = chestText.Substring(valueIndex, chestText.IndexOf('\n', valueIndex) - valueIndex);
            string soundId = line.Substring(line.IndexOf(':') + 1).Trim();

            Assert.AreEqual("metal_open", soundId, "chest.asset's open sound moved");

            string catalogPath = Path.Combine(root, "Assets/_PFE/Data/SoundCatalog.asset");
            Assert.IsTrue(File.Exists(catalogPath), $"missing {catalogPath}");
            Assert.IsTrue(File.ReadAllText(catalogPath).Contains("- id: metal_open"),
                "SoundCatalog has no clip for 'metal_open', so the open would be silent anyway");
        }

        /// <summary>
        /// The project root, found from the working directory first — <c>Application.dataPath</c> is a
        /// Unity ECall and an offline host cannot JIT a method that mentions it.
        /// </summary>
        private static string FindProjectRoot()
        {
            var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; i < 8 && directory != null; i++)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "Assets/_PFE")))
                {
                    return directory.FullName;
                }
                directory = directory.Parent;
            }
            return null;
        }
    }
}
