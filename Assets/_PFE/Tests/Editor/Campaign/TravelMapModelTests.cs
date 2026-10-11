using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Campaign;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// Asserts <see cref="TravelMapModel"/> against AS3 <c>PipPageInfo.as:143-205</c> (visibility),
    /// <c>Game.checkTravel</c> (<c>Game.as:483-506</c>, the <c>fin</c> gate) and
    /// <c>PipPageInfo.as:750-766</c> (the loaded gate).
    ///
    /// <para>The fixture deliberately never touches a <c>ScriptableObject</c>:
    /// <c>ScriptableObject.CreateInstance</c> is an ECall into Unity's native layer, so a fixture that
    /// used it would fail to *arrange* in the offline wall harness and prove nothing (rule #7). The
    /// production type <see cref="TravelLand"/> exists precisely so these rules stay reachable here.</para>
    /// </summary>
    [TestFixture]
    public class TravelMapModelTests
    {
        private static TravelLand Land(string id, bool isProb = false, bool isTest = false,
            int fin = 0, bool loaded = true)
        {
            return new TravelLand(id, isProb, isTest, fin, loaded);
        }

        private static TravelMapModel Model(IReadOnlyList<TravelLand> lands,
            LandRuntimeStateRegistry registry = null,
            System.Func<string, int> triggerLookup = null,
            System.Func<string> currentLandId = null,
            bool testMode = false)
        {
            return new TravelMapModel(lands, registry ?? new LandRuntimeStateRegistry(),
                triggerLookup, currentLandId, testMode);
        }

        // -------------------------------------------------------------------------------------------------
        //  prob lands
        // -------------------------------------------------------------------------------------------------

        [Test]
        public void ProbLand_IsNeverVisibleOrTravellable()
        {
            TravelLand prob = Land("prob", isProb: true);
            TravelLand ordinary = Land("begin");

            var registry = new LandRuntimeStateRegistry();
            registry.MarkVisited("prob");
            registry.SetAccess("prob", true);

            TravelMapModel model = Model(new[] { prob, ordinary }, registry);

            Assert.That(TravelMapModel.IsProbLand(prob), Is.True);
            Assert.That(model.IsVisible(prob), Is.False,
                "AS3 keeps the detached room collection in game.probs, not game.lands (Game.as:75-82).");
            Assert.That(model.CanTravel(prob), Is.False);

            // Positive control: the *same* registry state on a non-prob land does make it visible, so the
            // assertion above is about the prob flag and not about the registry being empty.
            registry.MarkVisited("begin");
            Assert.That(model.IsVisible(ordinary), Is.True);

            List<TravelLand> offered = model.OfferedLands();
            Assert.That(offered.Count, Is.EqualTo(1));
            Assert.That(offered[0].LandId, Is.EqualTo("begin"));
        }

        [Test]
        public void ProbLand_StaysHiddenEvenInTestMode()
        {
            // testMode short-circuits the visited/access lookup, so the prob test has to come first.
            // Without that ordering a prob land would pop into the sandbox map.
            TravelLand prob = Land("prob", isProb: true);
            TravelMapModel model = Model(new[] { prob }, testMode: true);

            Assert.That(model.IsVisible(prob), Is.False);
            Assert.That(model.OfferedLands(), Is.Empty);
        }

        // -------------------------------------------------------------------------------------------------
        //  visibility
        // -------------------------------------------------------------------------------------------------

        [Test]
        public void FreshSave_OffersNothingUntilALandIsVisited()
        {
            TravelLand begin = Land("begin");
            var registry = new LandRuntimeStateRegistry();
            TravelMapModel model = Model(new[] { begin }, registry);

            Assert.That(model.IsVisible(begin), Is.False,
                "A land the story has not unlocked is invisible, not merely greyed out.");

            registry.MarkVisited("begin");

            Assert.That(model.IsVisible(begin), Is.True);
            List<TravelLand> offered = model.OfferedLands();
            Assert.That(offered.Count, Is.EqualTo(1));
            Assert.That(offered[0].LandId, Is.EqualTo("begin"));
        }

        [Test]
        public void Access_UnlocksALandThatWasNeverVisited()
        {
            TravelLand surf = Land("surf");
            var registry = new LandRuntimeStateRegistry();
            TravelMapModel model = Model(new[] { surf }, registry);

            Assert.That(model.IsVisible(surf), Is.False);

            registry.SetAccess("surf", true); // AS3 `openland` (Script.as:458-468)

            Assert.That(model.IsVisible(surf), Is.True);
            Assert.That(registry.Get("surf").visited, Is.False,
                "`access` alone unlocks the map entry — it must not be silently recorded as a visit.");
        }

        [Test]
        public void TestLand_IsHiddenOutsideTestMode()
        {
            TravelLand sandbox = Land("test", isTest: true);
            var registry = new LandRuntimeStateRegistry();
            registry.MarkVisited("test");

            TravelMapModel normal = Model(new[] { sandbox }, registry, testMode: false);
            TravelMapModel testMode = Model(new[] { sandbox }, registry, testMode: true);

            Assert.That(normal.IsVisible(sandbox), Is.False,
                "A dev sandbox land is hidden unless test mode is on.");
            Assert.That(testMode.IsVisible(sandbox), Is.True);
        }

        [Test]
        public void TestMode_RevealsEveryOrdinaryLandEvenOnAFreshSave()
        {
            TravelLand a = Land("begin");
            TravelLand b = Land("surf");
            TravelMapModel model = Model(new[] { a, b }, testMode: true);

            Assert.That(model.OfferedLands().Count, Is.EqualTo(2));
        }

        // -------------------------------------------------------------------------------------------------
        //  the loaded gate
        // -------------------------------------------------------------------------------------------------

        [Test]
        public void CanTravel_RequiresTheRoomXmlToBeLoaded()
        {
            TravelLand notLoaded = Land("nio", loaded: false);
            TravelLand loaded = Land("surf", loaded: true);

            var registry = new LandRuntimeStateRegistry();
            registry.SetAccess("nio", true);
            registry.SetAccess("surf", true);

            TravelMapModel model = Model(new[] { notLoaded, loaded }, registry);

            Assert.That(model.IsVisible(notLoaded), Is.True);
            Assert.That(model.CanTravel(notLoaded), Is.False,
                "PipPageInfo.as:761 refuses travel unless the land is `loaded`.");

            // Positive control: the only difference is the loaded flag, and the loaded land does travel.
            Assert.That(model.CanTravel(loaded), Is.True);
            Assert.That(TravelMapModel.IsLoaded(notLoaded), Is.False);
            Assert.That(TravelMapModel.IsLoaded(loaded), Is.True);
        }

        // -------------------------------------------------------------------------------------------------
        //  the fin gate
        // -------------------------------------------------------------------------------------------------

        [Test]
        public void CheckTravel_GraveIsNeverTravellable()
        {
            TravelLand grave = Land("grave");
            TravelLand surf = Land("surf");

            TravelMapModel model = Model(new[] { grave, surf },
                triggerLookup: _ => 0,
                currentLandId: () => "grave");

            Assert.That(model.CheckTravel(grave), Is.False);
            Assert.That(model.CheckTravel(surf), Is.False,
                "The oracle tests the CURRENT land id (Game.as:485) — standing in `grave` locks the map.");

            // Positive control: the same two lands with the player standing elsewhere are travelable, so
            // the assertions above are about the current-land lock and not about the fin gate.
            TravelMapModel elsewhere = Model(new[] { grave, surf },
                triggerLookup: _ => 0,
                currentLandId: () => "surf");
            Assert.That(elsewhere.CheckTravel(surf), Is.True);
            Assert.That(elsewhere.CheckTravel(grave), Is.True);
        }

        [Test]
        public void CheckTravel_FinGateRestrictsTheFinaleStages()
        {
            TravelLand normal = Land("begin", fin: 0);
            TravelLand finale = Land("art", fin: 1);
            TravelLand epilogue = Land("grave", fin: 2);
            var lands = new[] { normal, finale, epilogue };

            // fin == 0 (the default): everything is reachable.
            TravelMapModel open = Model(lands, triggerLookup: _ => 0);
            Assert.That(open.CheckTravel(normal), Is.True);
            Assert.That(open.CheckTravel(finale), Is.True);
            Assert.That(open.CheckTravel(epilogue), Is.True);

            // fin == 1: only fin 0 and 1.
            TravelMapModel stage1 = Model(lands, triggerLookup: n => n == "fin" ? 1 : 0);
            Assert.That(stage1.CheckTravel(normal), Is.True);
            Assert.That(stage1.CheckTravel(finale), Is.True);
            Assert.That(stage1.CheckTravel(epilogue), Is.False, "Game.as:493-496.");

            // fin == 2: only fin 0 and 2.
            TravelMapModel stage2 = Model(lands, triggerLookup: n => n == "fin" ? 2 : 0);
            Assert.That(stage2.CheckTravel(normal), Is.True);
            Assert.That(stage2.CheckTravel(epilogue), Is.True);
            Assert.That(stage2.CheckTravel(finale), Is.False, "Game.as:497-500.");

            // fin == 3: only fin 2.
            TravelMapModel stage3 = Model(lands, triggerLookup: n => n == "fin" ? 3 : 0);
            Assert.That(stage3.CheckTravel(epilogue), Is.True);
            Assert.That(stage3.CheckTravel(normal), Is.False, "Game.as:501-504.");
            Assert.That(stage3.CheckTravel(finale), Is.False);
        }

        [Test]
        public void TravellableLands_IntersectsVisibilityWithTheTravelGate()
        {
            TravelLand begin = Land("begin", fin: 0);
            // fin == 2 so that the fin == 1 gate below genuinely refuses it: with fin == 1 the gate
            // allows `land.Fin == 0 || land.Fin == 1` and the land would (correctly) be travelable.
            TravelLand locked = Land("grave", fin: 2);

            var registry = new LandRuntimeStateRegistry();
            registry.MarkVisited("begin");
            registry.MarkVisited("grave");

            TravelMapModel model = Model(new[] { begin, locked }, registry,
                triggerLookup: n => n == "fin" ? 1 : 0);

            List<TravelLand> travellable = model.TravellableLands();
            Assert.That(travellable.Count, Is.EqualTo(1));
            Assert.That(travellable[0].LandId, Is.EqualTo("begin"));

            // Both lands are *visible*, so the single entry above really is the travel gate doing the
            // filtering and not the visibility rule.
            Assert.That(model.OfferedLands().Count, Is.EqualTo(2));
            Assert.That(model.CheckTravel(locked), Is.False, "Game.as:493-496 refuses fin 2 under fin 1.");
            Assert.That(model.CanTravel(begin), Is.True);
        }

        // -------------------------------------------------------------------------------------------------
        //  degenerate inputs
        // -------------------------------------------------------------------------------------------------

        [Test]
        public void NoLands_OffersNothingAndDoesNotThrow()
        {
            TravelMapModel nullLands = new TravelMapModel(null, null);
            Assert.That(nullLands.Lands, Is.Empty);
            Assert.That(nullLands.OfferedLands(), Is.Empty);
            Assert.That(nullLands.TravellableLands(), Is.Empty);
        }

        [Test]
        public void NullAsset_ProjectsToAProbLand()
        {
            // TravelLand.From is the only place that reads a land asset, and its null guard runs before any
            // property access — so it is reachable offline even though a non-null LandDefinition is not.
            TravelLand fromNull = TravelLand.From(null);

            Assert.That(fromNull.LandId, Is.Empty);
            Assert.That(fromNull.IsProb, Is.True, "An unreadable land must default to invisible.");
            Assert.That(fromNull.Loaded, Is.False);
            Assert.That(new TravelMapModel(new[] { fromNull }, null).IsVisible(fromNull), Is.False);
        }
    }
}
