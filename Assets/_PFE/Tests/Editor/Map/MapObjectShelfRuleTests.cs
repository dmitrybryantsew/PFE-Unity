using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using UnityEngine;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Pins AS3's prop-shelf rule (<c>Obj.shelf</c>) and the three unit-versus-prop searches that read
    /// it: landing on a prop, riding one, and being hurt by one.
    ///
    /// <para><b>Why these exist.</b> "I can't stand on a crate and I can't build a tower from crates"
    /// had no value to read anywhere in the port: there was no prop-level <c>shelf</c> field at all —
    /// only the <i>tile</i> flag of the same name, which is the one-way catwalk and a different thing.
    /// So both behaviours had to be built, and every number in the rule is one that is easy to get
    /// backwards in a way that looks reasonable:</para>
    ///
    /// <list type="bullet">
    /// <item>the default is <b>true</b>, not false, so this is not an opt-in list
    /// (<c>Box.as:22</c>);</item>
    /// <item>the width test is <b>strict</b> against one tile, so a 1-tile prop <i>passes</i>
    /// (<c>Box.as:269</c>);</item>
    /// <item>the authored attribute is applied <b>after</b> the clear rule and therefore overrides it
    /// (<c>Box.as:272-274</c>);</item>
    /// <item>and the landing test is <b>centre-in-span</b> for a prop, where the same-named unit
    /// function uses full overlap (<c>Box.as:1198</c> vs <c>Unit.as:2719</c>).</item>
    /// </list>
    ///
    /// <para><b>"I dropped a crate on an enemy and nothing happened" added the third search.</b>
    /// <c>Box.attDrop</c> (<c>Box.as:914-940</c>) is a fourth prop question with its own two gates, and
    /// its numbers invert just as easily: the ±5 px/frame pre-filter and the <c>vel2 &lt; 50</c>
    /// threshold are both in AS3's px-per-<b>frame</b> units while the layer integrates in px per
    /// second, so reading either against a px/s velocity makes it 900× too small — every settling crate
    /// would then wound whatever it touched. Those tests live in the "impact query" section
    /// below.</para>
    ///
    /// <para>AS3 authority: <c>fe/loc/Box.as</c>.</para>
    /// </summary>
    [TestFixture]
    public class MapObjectShelfRuleTests
    {
        const float TileSize = 40f;

        // ── The rule itself (pure, no room, no prop instance) ────────────────────────────────

        [Test]
        public void OrdinaryCrate_IsShelfByDefault()
        {
            // Box.as:22 — `public var shelf:Boolean = true;`. The single most important assertion in
            // this file: a port that reads `shelf` as an opt-in capability gets this false and is then
            // wrong about nearly every prop in the game.
            Assert.IsTrue(MapObjectShelfRule.IsShelf(
                    hasAuthoredShelfAttribute: false, isWall: false, widthPixels: 2f * TileSize),
                "An 80 px crate is a floor. The oracle's default is TRUE and only a narrow or wall prop " +
                "is cleared (Box.as:22, :269-272).");
        }

        [Test]
        public void PropNarrowerThanOneTile_IsNotShelf()
        {
            // Box.as:269 — `if(scX < 40 || this.wall > 0) this.shelf = false;`
            Assert.IsFalse(MapObjectShelfRule.IsShelf(false, false, TileSize - 1f),
                "39 px is narrower than a tile, so nothing can stand on it.");
        }

        [Test]
        public void ExactlyOneTileWide_IsShelf()
        {
            // The boundary. AS3 compares with `<`, not `<=`, so a prop of exactly one tile PASSES.
            // Writing this as `<=`, or as "needs two tiles", makes every single-tile crate
            // non-standable — a silent, plausible-looking inversion.
            Assert.IsTrue(MapObjectShelfRule.IsShelf(false, false, TileSize),
                "`scX < 40` is strict: 40 px is not less than 40, so a 1-tile prop is a floor.");

            Assert.That(MapObjectShelfRule.MinimumShelfWidthPixels, Is.EqualTo(TileSize).Within(1e-3f),
                "The threshold is one tile — Box.as:269's literal 40 against World.tileX.");
        }

        [Test]
        public void AuthoredShelfAttribute_OverridesTheWidthRule()
        {
            // Box.as:272-274 — the authored attribute is applied AFTER the clear rule, so it can
            // restore a prop the width test had just cleared. Order matters: treating the attribute as
            // the only source gives the same answer here and the wrong answer for every prop that does
            // not author it.
            Assert.IsTrue(MapObjectShelfRule.IsShelf(true, false, 10f),
                "An authored `shelf` wins even for a 10 px prop.");

            // ...and it also beats the wall rule, because AS3 tests wall first and the attribute second.
            Assert.IsTrue(MapObjectShelfRule.IsShelf(true, true, 10f),
                "The attribute is the last word — Box.as:272 runs after both clears.");
        }

        [Test]
        public void WallGeometry_IsNotShelf()
        {
            // Box.as:269-272 — `this.wall > 0` clears it.
            Assert.IsFalse(MapObjectShelfRule.IsShelf(false, true, 4f * TileSize),
                "A wall is not a platform however wide it is.");
        }

        [Test]
        public void UnmeasuredWidth_IsNotShelf()
        {
            // A non-positive width means "not measured", and an unmeasured prop must not silently
            // become a floor — the failure mode of guessing here is a crate you cannot fall through
            // rather than one you cannot stand on.
            Assert.IsFalse(MapObjectShelfRule.IsShelf(false, false, 0f));
            Assert.IsFalse(MapObjectShelfRule.IsShelf(false, false, -1f));
        }

        [Test]
        public void WidthResolution_PrefersTheAuthoredSpriteWidth()
        {
            // Box.as:158 `scX = vis.width`, overridden by :162 `scX = node.@scx`.
            Assert.AreEqual(55f, MapObjectShelfRule.ResolveWidthPixels("55", 2), 1e-3f,
                "An authored scx is the sprite width and wins.");

            Assert.AreEqual(80f, MapObjectShelfRule.ResolveWidthPixels(null, 2), 1e-3f,
                "Without one, the footprint in tiles is the stand-in.");

            Assert.AreEqual(80f, MapObjectShelfRule.ResolveWidthPixels("not-a-number", 2), 1e-3f,
                "Unparseable is treated as absent, not as zero.");
        }

        // ── The prop-instance view of the rule ───────────────────────────────────────────────

        [Test]
        public void PropInstance_ReadsShelfFromItsAttributes()
        {
            ObjectInstance crate = MakeProp("mcrate1", sizeTiles: 2, heightTiles: 2);
            Assert.IsTrue(crate.IsShelf(),
                "A 2-tile crate with no definition still resolves the rule from its own attributes.");

            ObjectInstance sliver = MakeProp("sliver", sizeTiles: 1, heightTiles: 1, authoredScx: "20");
            Assert.IsFalse(sliver.IsShelf(), "An authored scx of 20 px is narrower than a tile.");
        }

        // ── Box.checkShelf: what a falling prop may land on ───────────────────────────────────

        [Test]
        public void FallingProp_FindsAShelfPropBelowIt()
        {
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance support = MakeProp("support", 2, 2);
            support.position = new Vector2(200f, 100f);      // bottom 100, top 180
            layer.Register(support);
            AtRest(support);

            // The falling crate's bottom is 5 px ABOVE the support's top, and this substep moves it
            // 9 px down — so it crosses the top. Both halves of the crossing test have to hold, and a
            // step that does not reach (or a crate already below) is the negative control in
            // `FallingProp_IgnoresASupportThisStepDoesNotReach` / `..._AlreadyBelow`.
            ObjectInstance falling = MakeProp("falling", 2, 2);
            falling.position = new Vector2(200f, 185f);
            layer.Register(falling);

            bool found = layer.TryFindSupportProp(falling, -9f, out ObjectInstance landedOn, out float surfaceY);

            Assert.IsTrue(found, "A crate whose bottom is 5 px above a resting crate's top and which " +
                                 "falls 9 px this substep must land on it — this is the whole of " +
                                 "'I can't build a tower'.");
            Assert.AreSame(support, landedOn);
            Assert.AreEqual(180f, surfaceY, 1e-3f,
                "The landing surface is the SUPPORT'S TOP edge (Box.as:1200 returns box.Y1), not its " +
                "position — position is the bottom in the port too, so returning it would bury the " +
                "rider half a crate deep.");
        }

        [Test]
        public void FallingProp_IgnoresASupportThatIsStillMoving()
        {
            // Box.as:1198 — `_loc3_.stay` is required. A crate still settling is not a floor yet;
            // without this a tower can be built out of crates that are all still falling.
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance support = MakeProp("support", 2, 2);
            support.position = new Vector2(200f, 100f);
            support.runtimeState.dynamicState.stay = false;
            layer.Register(support);

            ObjectInstance falling = MakeProp("falling", 2, 2);
            falling.position = new Vector2(200f, 300f);
            layer.Register(falling);

            Assert.IsFalse(layer.TryFindSupportProp(falling, -20f, out _, out _),
                "A support that is not `stay` is refused.");
        }

        [Test]
        public void FallingProp_IgnoresASupportHeldByTelekinesis()
        {
            // Box.as:1198 — `!_loc4_.levit`. A crate floating in mid-air is not a platform.
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance support = MakeProp("support", 2, 2);
            support.position = new Vector2(200f, 100f);
            layer.Register(support);
            AtRest(support);
            support.runtimeState.dynamicState.isHeldByTelekinesis = true;

            ObjectInstance falling = MakeProp("falling", 2, 2);
            falling.position = new Vector2(200f, 300f);
            layer.Register(falling);

            Assert.IsFalse(layer.TryFindSupportProp(falling, -20f, out _, out _),
                "A held crate is not a floor.");
        }

        [Test]
        public void FallingProp_IgnoresASupportItIsAlreadyBelow()
        {
            // The `Y2 <= box.Y1` half of the crossing test. A prop already underneath the support's top
            // must not be teleported up onto it — that is the shape of a rider being yanked upward
            // every tick.
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance support = MakeProp("support", 2, 2);
            support.position = new Vector2(200f, 100f);      // top 180
            layer.Register(support);
            AtRest(support);

            ObjectInstance below = MakeProp("below", 2, 2);
            below.position = new Vector2(200f, 120f);        // bottom 120, already under the top
            layer.Register(below);

            Assert.IsFalse(layer.TryFindSupportProp(below, -20f, out _, out _),
                "Already at or below the support's top: nothing to land on.");
        }

        [Test]
        public void FallingProp_IgnoresASupportThisStepDoesNotReach()
        {
            // The `Y2 + dy > box.Y1` half: the step must actually cross the top. A 5 px step toward a
            // top 120 px away is not a landing, and snapping it there would be teleportation.
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance support = MakeProp("support", 2, 2);
            support.position = new Vector2(200f, 100f);      // top 180
            layer.Register(support);
            AtRest(support);

            ObjectInstance falling = MakeProp("falling", 2, 2);
            falling.position = new Vector2(200f, 300f);      // bottom 300
            layer.Register(falling);

            Assert.IsFalse(layer.TryFindSupportProp(falling, -5f, out _, out _),
                "A 5 px step does not reach a top 120 px below, so it is not a landing.");
        }

        [Test]
        public void FallingProp_HangingHalfOffTheEdge_DoesNotLand()
        {
            // ── The negative control that separates this from Unit.checkShelf ────────────────────
            //
            // Box.as:1198 tests `!(X < box.X1 || X > box.X2)` — the prop's CENTRE must be over the
            // support. Unit.as:2719 tests `!(X2 < box.X1 || X1 > box.X2)` — full AABB OVERLAP. So a crate
            // with only its edge over another crate does NOT land on it (it falls), while a unit in the
            // same position does.
            //
            // Sharing one overlap helper between the two would silently change which of them is true.
            // This test is what fails if someone later "deduplicates" them.
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance support = MakeProp("support", 2, 2);
            support.position = new Vector2(200f, 100f);      // x span 160..240, top 180
            layer.Register(support);
            AtRest(support);

            ObjectInstance falling = MakeProp("falling", 2, 2);
            // Centre at x = 250, i.e. 10 px past the support's right edge (240). The boxes still
            // overlap by 30 px horizontally, so an OVERLAP test would say "yes".
            falling.position = new Vector2(250f, 300f);
            layer.Register(falling);

            Assert.IsFalse(layer.TryFindSupportProp(falling, -200f, out _, out _),
                "The crate's centre is past the support's edge, so it falls past it — even though the " +
                "boxes overlap. This is Box.as:1198's centre test, not Unit.as:2719's overlap test.");

            // Positive control for the same geometry: move the centre back inside the span and the same
            // call must now find it. Without this, the assertion above would pass for a rule that simply
            // never finds anything.
            falling.position = new Vector2(230f, 300f);
            Assert.IsTrue(layer.TryFindSupportProp(falling, -200f, out _, out _),
                "Same boxes, centre inside the span: now it lands. Proves the test above is measuring " +
                "the centre rule and not a blanket refusal.");
        }

        // ── Box.as:571-595: a rider follows its support ───────────────────────────────────────

        [Test]
        public void Rider_IsCarriedByItsSupportsDisplacement()
        {
            RoomInstance room = MakeEmptyRoom();
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();

            ObjectInstance support = MakeProp("support", 2, 2);
            support.position = new Vector2(200f, 100f);
            AtRest(support);
            // The support "moved 12 px to the right last tick". Displacement, not velocity: a support
            // that was refused a move has a velocity and no displacement, and carrying a rider by the
            // velocity would slide it off a crate that never moved.
            support.runtimeState.dynamicState.cdx = 12f;

            ObjectInstance rider = MakeProp("rider", 2, 2);
            rider.position = new Vector2(200f, 180f);        // sitting exactly on the support's top
            AtRest(rider);
            rider.runtimeState.dynamicState.osnova = support;

            // Only the RIDER is stepped. The support is deliberately left out of the layer and the room
            // so its hand-written `cdx` survives the tick: if it were stepped too, its own (zero)
            // displacement would overwrite the value under test and the assertion would be measuring
            // the support's gravity instead of the carry.
            room.objects.Add(rider);
            layer.Update(room, 1f / 30f);

            Assert.AreEqual(212f, rider.position.x, 0.5f,
                "The rider travels with its support — Box.as:590 `X += this.osnova.cdx`.");
        }

        [Test]
        public void Rider_IsReleasedWhenItsSupportIsPickedUp()
        {
            // Box.as:577-580 — `if(!this.osnova.stay || this.osnova.levit) { stay = false; osnova = null; }`
            // A crate you lift out of a stack must stop carrying the crate that was on top of it.
            RoomInstance room = MakeEmptyRoom();
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();

            ObjectInstance support = MakeProp("support", 2, 2);
            support.position = new Vector2(200f, 100f);
            AtRest(support);
            support.runtimeState.dynamicState.cdx = 12f;
            support.runtimeState.dynamicState.isHeldByTelekinesis = true;

            ObjectInstance rider = MakeProp("rider", 2, 2);
            rider.position = new Vector2(200f, 180f);
            AtRest(rider);
            rider.runtimeState.dynamicState.osnova = support;

            room.objects.Add(rider);
            layer.Update(room, 1f / 30f);

            Assert.IsNull(rider.runtimeState.dynamicState.osnova,
                "The link is dropped when the support is levitated.");
            Assert.IsFalse(rider.runtimeState.dynamicState.stay,
                "...and the rider is no longer at rest — it is about to fall, and AS3 says so at :577.");
        }

        [Test]
        public void HeldProp_ForgetsItsSupport()
        {
            // Box.as:571-575 — while levit, `stay = fixPlav = false; osnova = null`. A crate you pick up
            // out of a stack must stop being carried by the crate it just left.
            RoomInstance room = MakeEmptyRoom();
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();

            ObjectInstance support = MakeProp("support", 2, 2);
            support.position = new Vector2(200f, 100f);
            AtRest(support);
            support.runtimeState.dynamicState.cdx = 12f;

            ObjectInstance rider = MakeProp("rider", 2, 2);
            rider.position = new Vector2(200f, 180f);
            AtRest(rider);
            rider.runtimeState.dynamicState.osnova = support;
            rider.runtimeState.dynamicState.isHeldByTelekinesis = true;

            room.objects.Add(rider);
            layer.Update(room, 1f / 30f);

            Assert.IsNull(rider.runtimeState.dynamicState.osnova,
                "A lifted crate forgets what it was standing on.");
        }

        // ── Unit.as:2713-2741: a UNIT lands on a prop (the other shelf search) ────────────────

        [Test]
        public void UnitLanding_StepCrossesAPropTop_Lands()
        {
            // The headline behaviour of the whole change: "I can't stand on a crate". Feet at 185 fall to
            // 176 and the crate's top is at 180, so the step crosses it.
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance support = MakeProp("support", 2, 2);
            support.position = new Vector2(200f, 100f);      // x 160..240, top 180
            layer.Register(support);
            AtRest(support);

            Assert.IsTrue(
                layer.TryFindPropCrossedThisStep(FeetRect(180f, 185f), 176f, out ObjectInstance landed, out float surface),
                "A unit whose feet cross a crate's top edge lands on it.");

            Assert.AreSame(support, landed, "…and the crate it landed on is the one reported.");
            Assert.That(surface, Is.EqualTo(180f).Within(1e-4f),
                "…and the surface is the crate's top edge, which is where the caller snaps the feet.");
        }

        [Test]
        public void UnitLanding_StepDoesNotReachTheProp_DoesNotLand()
        {
            // Negative control on the same geometry: 3 px of a 5 px gap. Without this, a rule that only
            // checked "is the surface below me" would pass the test above.
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance support = MakeProp("support", 2, 2);
            support.position = new Vector2(200f, 100f);
            layer.Register(support);
            AtRest(support);

            Assert.IsFalse(
                layer.TryFindPropCrossedThisStep(FeetRect(180f, 185f), 182f, out _, out _),
                "A step that stops 2 px above the top has not landed.");
        }

        [Test]
        public void UnitLanding_HangingHalfOffTheEdge_StillLands()
        {
            // ── The positive control that separates the two shelf searches ───────────────────────
            //
            // Unit.as:2719 tests full AABB OVERLAP, where Box.as:1198 tests whether the box's CENTRE is
            // over the support. So a unit with only its edge on a crate DOES stand on it, while a crate
            // in exactly the same position falls past it (see
            // FallingProp_HangingHalfOffTheEdge_DoesNotLand above, which asserts the opposite answer for
            // the same geometry).
            //
            // Merging the two searches into one overlap helper would silently break whichever one is
            // not chosen. This test is what fails if that happens.
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance support = MakeProp("support", 2, 2);
            support.position = new Vector2(200f, 100f);      // x span 160..240
            layer.Register(support);
            AtRest(support);

            // Feet span 225..265: 15 px of overlap, centre at 245 — PAST the support's right edge (240).
            // A centre-in-span test refuses this; the unit rule accepts it.
            Assert.IsTrue(
                layer.TryFindPropCrossedThisStep(FeetRect(225f, 185f), 176f, out _, out float surface),
                "A unit stands on a crate it is only half on — Unit.as:2719 is an OVERLAP test, not " +
                "Box.as:1198's centre test.");

            Assert.That(surface, Is.EqualTo(180f).Within(1e-4f));

            // Negative control for the overlap half: slide the unit fully clear of the crate and the
            // same call must refuse. Without this the assertion above would pass for a rule with no
            // horizontal test at all.
            Assert.IsFalse(
                layer.TryFindPropCrossedThisStep(FeetRect(241f, 185f), 176f, out _, out _),
                "One pixel clear of the crate's right edge: no overlap, so no landing.");
        }

        [Test]
        public void UnitLanding_OnAPropThatIsStillMoving_StillLands()
        {
            // ── The `stay` divergence, from the unit side ────────────────────────────────────────
            //
            // Box.checkShelf requires the support to be at rest (`Box.as:1198`); Unit.checkShelf does
            // NOT (`Unit.as:2719` has no `stay` term) — a unit may land on a crate that is still
            // settling, and pushes it down (Unit.as:2734-2737). So the support here is deliberately
            // left NOT at rest.
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance support = MakeProp("support", 2, 2);
            support.position = new Vector2(200f, 100f);
            layer.Register(support);

            Assert.IsFalse(support.runtimeState.dynamicState.stay,
                "Precondition: the support is still moving.");

            Assert.IsTrue(
                layer.TryFindPropCrossedThisStep(FeetRect(180f, 185f), 176f, out _, out _),
                "A unit lands on a crate that has not settled — there is no `stay` term on this side.");
        }

        [Test]
        public void UnitLanding_OnAPropHeldByTelekinesis_DoesNotLand()
        {
            // Unit.as:2719 — `!_loc4_.levit`. A crate floating in mid-air is not a platform. This is the
            // one gate the two searches SHARE, so it is asserted on both.
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance support = MakeProp("support", 2, 2);
            support.position = new Vector2(200f, 100f);
            layer.Register(support);
            AtRest(support);
            support.runtimeState.dynamicState.isHeldByTelekinesis = true;

            Assert.IsFalse(
                layer.TryFindPropCrossedThisStep(FeetRect(180f, 185f), 176f, out _, out _),
                "A held crate is not a floor, for a unit any more than for a crate.");
        }

        // ── The state form vs the event form: why both exist ────────────────────────────────

        [Test]
        public void StateQuery_UnitWithinTheStepUpBand_IsGrounded()
        {
            // UnitController.ResolveGroundState runs BEFORE its step, so it asks the state question with
            // a step-up band (porog = 10, the same allowance the tile query uses). Feet 8 px above the
            // top is inside that band.
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance support = MakeProp("support", 2, 2);
            support.position = new Vector2(200f, 100f);      // top 180
            layer.Register(support);
            AtRest(support);

            Assert.IsTrue(
                layer.TryFindGroundPropUnder(FeetRect(180f, 188f), 10f, out ObjectInstance found, out float surface),
                "8 px above the top is within a 10 px step-up allowance.");

            Assert.AreSame(support, found);
            Assert.That(surface, Is.EqualTo(180f).Within(1e-4f));
        }

        [Test]
        public void StateQuery_UnitBeyondTheStepUpBand_IsNotGrounded()
        {
            // Negative control: 11 px above is outside the band, so the unit is in the air above the
            // crate, not on it. Without this the band would be "anywhere above".
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance support = MakeProp("support", 2, 2);
            support.position = new Vector2(200f, 100f);
            layer.Register(support);
            AtRest(support);

            Assert.IsFalse(
                layer.TryFindGroundPropUnder(FeetRect(180f, 191f), 10f, out _, out _),
                "11 px above the top is beyond a 10 px allowance — the unit is in the air.");
        }

        [Test]
        public void EventQuery_UnitWellAboveAProp_WithATinyStep_DoesNotSnapUp()
        {
            // ── Why the motor cannot use the band form ──────────────────────────────────────────
            //
            // The band answers "is there a surface within porog below my feet", which for a motor would
            // mean: a player whose feet are 8 px above a crate and who moves 1 px gets snapped UP onto
            // it. Every prop within porog would behave like a magnet, and a player could never stand in
            // the gap above a crate.
            //
            // The crossing form refuses, because the step did not reach the top. This test is the reason
            // RoomObjectPhysicsLayer has two entry points over one loop rather than one.
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance support = MakeProp("support", 2, 2);
            support.position = new Vector2(200f, 100f);      // top 180
            layer.Register(support);
            AtRest(support);

            Assert.IsFalse(
                layer.TryFindPropCrossedThisStep(FeetRect(180f, 188f), 187f, out _, out _),
                "Feet 8 px above the top, moving 1 px: not a landing. The band form would say yes, " +
                "which is exactly the magnet behaviour this form exists to avoid.");

            // Positive control on the same geometry: keep the feet where they are and let the step span
            // the top, and the same call must now land. Proves the refusal above is the crossing test and
            // not a blanket "8 px above is too high" rule.
            Assert.IsTrue(
                layer.TryFindPropCrossedThisStep(FeetRect(180f, 188f), 179f, out _, out _),
                "Same start, but the step spans the top edge: now it lands.");
        }

        // ── The impact query (AS3 Box.attDrop :914-940, unit-driven) ─────────────────────────

        /// <summary>
        /// A prop moving at 20 px/frame that overlaps the unit is found. 20 px/frame is the
        /// <c>World.maxdy</c> terminal fall speed, so this is "a crate falling as fast as a crate
        /// can".
        /// </summary>
        [Test]
        public void ImpactQuery_FastOverlappingProp_IsFound()
        {
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance crate = MakeProp("crate", 2, 2);
            crate.position = new Vector2(200f, 100f);            // bounds x[160,240] y[100,180]
            layer.Register(crate);

            // The layer integrates in px/SECOND; AS3's gates are in px per 30 Hz FRAME. 20 px/frame is
            // 600 px/s, and getting this conversion wrong in either direction is the single most likely
            // mistake in this whole path.
            crate.runtimeState.dynamicState.velocity = new Vector2(0f, 600f);

            // Unit rect overlapping the crate: feet at the crate's bottom, 40 x 60 px.
            var unitBounds = new Rect(180f, 100f, 40f, 60f);

            Assert.IsTrue(
                layer.TryFindImpactingPropFor(unitBounds, out ObjectInstance found, out float vel2),
                "A crate at terminal velocity overlapping the unit must be found.");

            Assert.AreSame(crate, found);
            Assert.That(vel2, Is.EqualTo(400f).Within(1e-3f),
                "20 px/frame squared is 400, and it is returned so the caller's damage formula uses " +
                "the value the gate accepted rather than recomputing it.");
        }

        /// <summary>
        /// Below <c>Box.as:632</c>'s ±5 px/frame pre-filter the sweep never runs at all. 4 px/frame is
        /// 120 px/s.
        /// </summary>
        [Test]
        public void ImpactQuery_PropBelowTheSweepGate_IsNotFound()
        {
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance crate = MakeProp("crate", 2, 2);
            crate.position = new Vector2(200f, 100f);
            layer.Register(crate);
            crate.runtimeState.dynamicState.velocity = new Vector2(0f, 120f);   // 4 px/frame

            Assert.IsFalse(
                layer.TryFindImpactingPropFor(new Rect(180f, 100f, 40f, 60f), out _, out _),
                "4 px/frame is under the 5 px/frame pre-filter, so a settling crate does not wound " +
                "whatever it happens to be resting against.");
        }

        /// <summary>
        /// Exactly 5 px/frame does <b>not</b> sweep — the oracle's four comparisons are all strict.
        /// 150 px/s.
        /// </summary>
        [Test]
        public void ImpactQuery_PropAtExactlyFivePixelsPerFrame_IsNotFound()
        {
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance crate = MakeProp("crate", 2, 2);
            crate.position = new Vector2(200f, 100f);
            layer.Register(crate);
            crate.runtimeState.dynamicState.velocity = new Vector2(0f, 150f);   // exactly 5 px/frame

            Assert.IsFalse(
                layer.TryFindImpactingPropFor(new Rect(180f, 100f, 40f, 60f), out _, out _),
                "Box.as:632 is `dy > 5 || dy < -5`, so exactly 5 on an axis is refused. This is the " +
                "boundary the PropImpactMath fixture pins in isolation; here it is pinned through the " +
                "query, because that is where a > / >= slip would actually change gameplay.");
        }

        /// <summary>
        /// <b>Fast enough to sweep but too soft to hurt.</b> 6 px/frame clears the ±5 pre-filter but its
        /// <c>vel2</c> is 36, under the 50 threshold at <c>Box.as:918</c>. This is the one test that
        /// isolates the second gate — with only the two tests above, a query that checked the pre-filter
        /// and stopped would still pass.
        /// </summary>
        [Test]
        public void ImpactQuery_PropPastTheSweepGateButUnderTheVel2Threshold_IsNotFound()
        {
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance crate = MakeProp("crate", 2, 2);
            crate.position = new Vector2(200f, 100f);
            layer.Register(crate);
            crate.runtimeState.dynamicState.velocity = new Vector2(0f, 180f);   // 6 px/frame, vel2 = 36

            Assert.IsFalse(
                layer.TryFindImpactingPropFor(new Rect(180f, 100f, 40f, 60f), out _, out _),
                "6 px/frame sweeps (6 > 5) but vel2 is 36 < 50, so Box.as:918 refuses the impact. The " +
                "two gates are not the same test and this is the case that separates them.");
        }

        /// <summary>A prop that is not touching the unit is not an impact, however fast it is.</summary>
        [Test]
        public void ImpactQuery_FastPropThatDoesNotOverlap_IsNotFound()
        {
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance crate = MakeProp("crate", 2, 2);
            crate.position = new Vector2(200f, 100f);            // bounds x[160,240] y[100,180]
            layer.Register(crate);
            crate.runtimeState.dynamicState.velocity = new Vector2(0f, 600f);

            // Well clear on x, overlapping on y: the AABB test must require BOTH axes.
            Assert.IsFalse(
                layer.TryFindImpactingPropFor(new Rect(400f, 100f, 40f, 60f), out _, out _),
                "Overlapping on y but not x is not an overlap.");

            // ...and clear on y, overlapping on x, for the other axis.
            Assert.IsFalse(
                layer.TryFindImpactingPropFor(new Rect(180f, 300f, 40f, 60f), out _, out _),
                "Overlapping on x but not y is not an overlap either — a single-axis test would pass " +
                "one of these two and fail the other.");
        }

        /// <summary>
        /// A crate being carried by telekinesis is a payload, not a projectile — AS3 <c>Box.as:630</c>'s
        /// <c>!levit</c>.
        /// </summary>
        [Test]
        public void ImpactQuery_PropHeldByTelekinesis_IsNotFound()
        {
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance crate = MakeProp("crate", 2, 2);
            crate.position = new Vector2(200f, 100f);
            layer.Register(crate);
            crate.runtimeState.dynamicState.velocity = new Vector2(0f, 600f);
            crate.runtimeState.dynamicState.isHeldByTelekinesis = true;

            Assert.IsFalse(
                layer.TryFindImpactingPropFor(new Rect(180f, 100f, 40f, 60f), out _, out _),
                "A crate you are levitating must not damage what you wave it through. This is also the " +
                "positive control for the test above: same geometry, same speed, only the flag differs.");
        }

        /// <summary>
        /// With two qualifying props the <b>harder</b> one is returned — which is equivalent to AS3
        /// hitting the unit with whichever prop's update ran first, because <c>udarBox</c> grants
        /// <c>neujaz</c> and the second prop is then refused anyway.
        /// </summary>
        [Test]
        public void ImpactQuery_TwoQualifyingProps_ReturnsTheHarder()
        {
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();

            ObjectInstance slow = MakeProp("slow", 2, 2);
            slow.position = new Vector2(200f, 100f);
            layer.Register(slow);
            slow.runtimeState.dynamicState.velocity = new Vector2(0f, 600f);     // 20 px/frame, vel2 400

            ObjectInstance fast = MakeProp("fast", 2, 2);
            fast.position = new Vector2(200f, 100f);
            layer.Register(fast);
            fast.runtimeState.dynamicState.velocity = new Vector2(0f, 900f);     // 30 px/frame, vel2 900

            var unitBounds = new Rect(180f, 100f, 40f, 60f);

            Assert.IsTrue(layer.TryFindImpactingPropFor(unitBounds, out ObjectInstance found, out float vel2));
            Assert.AreSame(fast, found, "The harder prop wins.");
            Assert.That(vel2, Is.EqualTo(900f).Within(1e-3f), "And its vel2 is the one reported.");

            // Registration order must not decide it — the same geometry with the two props registered
            // the other way round has to give the same answer, or the result would depend on the room's
            // object order (which is what AS3's prop-driven loop does, and the one thing this form is
            // meant to remove).
            RoomObjectPhysicsLayer reversed = new RoomObjectPhysicsLayer();
            ObjectInstance fastFirst = MakeProp("fast", 2, 2);
            fastFirst.position = new Vector2(200f, 100f);
            reversed.Register(fastFirst);
            fastFirst.runtimeState.dynamicState.velocity = new Vector2(0f, 900f);

            ObjectInstance slowSecond = MakeProp("slow", 2, 2);
            slowSecond.position = new Vector2(200f, 100f);
            reversed.Register(slowSecond);
            slowSecond.runtimeState.dynamicState.velocity = new Vector2(0f, 600f);

            Assert.IsTrue(reversed.TryFindImpactingPropFor(unitBounds, out ObjectInstance found2, out _));
            Assert.AreSame(fastFirst, found2, "Still the harder prop, whichever order it was registered.");
        }

        /// <summary>
        /// The gate is symmetric — a crate launched <i>upward</i> fast is as dangerous as one falling.
        /// AS3 tests <c>dy &gt; 5 || dy &lt; -5</c>, so a prop moving up is not exempt.
        /// </summary>
        [Test]
        public void ImpactQuery_PropMovingUpwardFast_IsFound()
        {
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance crate = MakeProp("crate", 2, 2);
            crate.position = new Vector2(200f, 100f);
            layer.Register(crate);
            crate.runtimeState.dynamicState.velocity = new Vector2(0f, -600f);   // 20 px/frame upward

            Assert.IsTrue(
                layer.TryFindImpactingPropFor(new Rect(180f, 100f, 40f, 60f), out _, out float vel2),
                "Box.as:632's negative branch — `dy < -5` — means an upward-moving crate is not " +
                "exempt. Dropping the negative branch is the mutation this test exists to catch.");

            Assert.That(vel2, Is.EqualTo(400f).Within(1e-3f), "The sign of the velocity does not affect vel2.");
        }

        /// <summary>
        /// <b>The only point where the two gates disagree, exercised through the query.</b> A prop at
        /// exactly 5 px/frame on <i>both</i> axes has <c>vel2 = 50</c>, which <c>Box.as:918</c> would
        /// <b>pass</b> — but <c>Box.as:632</c>'s pre-filter refuses the sweep before <c>:918</c> is ever
        /// reached, because all four of its comparisons are strict.
        /// </summary>
        /// <remarks>
        /// <para><b>This test exists because a mutation run found the sweep gate untested.</b> The two
        /// tests above use 4 and 5 px/frame on <i>one</i> axis, where <c>vel2</c> is 16 and 25 — both
        /// under 50 — so the <c>vel2</c> gate refuses them and the sweep gate is never reached. Deleting
        /// the sweep gate entirely left every test green. Only the corner separates them.</para>
        /// </remarks>
        [Test]
        public void ImpactQuery_PropAtTheExactSweepCorner_IsNotFound()
        {
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance crate = MakeProp("crate", 2, 2);
            crate.position = new Vector2(200f, 100f);
            layer.Register(crate);

            // 5 px/frame on each axis, i.e. 150 px/s. vel2 is 5² + 5² = exactly 50.
            crate.runtimeState.dynamicState.velocity = new Vector2(150f, 150f);

            var unitBounds = new Rect(180f, 100f, 40f, 60f);

            Assert.IsFalse(
                layer.TryFindImpactingPropFor(unitBounds, out _, out _),
                "vel2 is exactly 50, so Box.as:918 alone would let this through — but Box.as:632 is " +
                "`dy > 5 || dy < -5 || dx > 5 || dx < -5`, and at exactly 5 on both axes all four are " +
                "false, so the sweep never runs. The two gates are not interchangeable.");

            // Positive control on the same geometry: a hair more on one axis and the same prop IS found.
            // Without this the refusal above would be indistinguishable from "this geometry is never
            // found for some other reason".
            crate.runtimeState.dynamicState.velocity = new Vector2(150f, 150.1f);

            Assert.IsTrue(
                layer.TryFindImpactingPropFor(unitBounds, out _, out float vel2),
                "5.003 px/frame on y clears the strict comparison, so the sweep runs and vel2 (50.03) " +
                "clears the second gate.");

            Assert.That(vel2, Is.GreaterThanOrEqualTo(50f),
                "The positive control must pass the vel2 gate on its own merits, or it is not a control " +
                "for the sweep gate.");
        }

        /// <summary>
        /// A <b>destroyed</b> prop does not hurt anyone — AS3's <c>invis</c> (<c>Box.as:1059</c>, set
        /// when a prop falls out of the room's bottom), which is the port's
        /// <c>isDestroyed</c>/<c>!isActive</c> pair.
        /// </summary>
        /// <remarks>
        /// A prop stays in the layer's tracked list for the tick it dies, so this is a reachable state
        /// and not a defensive guard: without it a crate that fell out of the world would still land its
        /// final <c>vel2</c> on whatever it passed on the way down.
        /// </remarks>
        [Test]
        public void ImpactQuery_DestroyedProp_IsNotFound()
        {
            RoomObjectPhysicsLayer layer = new RoomObjectPhysicsLayer();
            ObjectInstance crate = MakeProp("crate", 2, 2);
            crate.position = new Vector2(200f, 100f);
            layer.Register(crate);
            crate.runtimeState.dynamicState.velocity = new Vector2(0f, 600f);
            crate.runtimeState.isDestroyed = true;

            Assert.IsFalse(
                layer.TryFindImpactingPropFor(new Rect(180f, 100f, 40f, 60f), out _, out _),
                "A destroyed prop is invisible and must not damage — the same geometry and speed as " +
                "ImpactQuery_FastOverlappingProp_IsFound, with only the destroyed flag added.");

            // And the other half of the same gate.
            crate.runtimeState.isDestroyed = false;
            crate.isActive = false;

            Assert.IsFalse(
                layer.TryFindImpactingPropFor(new Rect(180f, 100f, 40f, 60f), out _, out _),
                "An inactive prop is likewise not a threat.");
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A unit's collider AABB in room-local pixels, with <c>yMin</c> at the feet — the convention
        /// both shelf searches take. 40 × 60 px, the footprint of an ordinary unit.
        /// </summary>
        static Rect FeetRect(float left, float feetY, float width = 40f, float height = 60f)
        {
            return new Rect(left, feetY, width, height);
        }

        static ObjectInstance MakeProp(string id, int sizeTiles, int heightTiles, string authoredScx = null)
        {
            var prop = new ObjectInstance
            {
                objectId = id,
                objectType = "box",
                attributes = new System.Collections.Generic.List<MapObjectAttributeData>
                {
                    new MapObjectAttributeData { key = "size", value = sizeTiles.ToString() },
                    new MapObjectAttributeData { key = "wid", value = heightTiles.ToString() },
                    new MapObjectAttributeData { key = "tip", value = "box" },
                },
            };

            if (authoredScx != null)
            {
                prop.attributes.Add(new MapObjectAttributeData { key = "scx", value = authoredScx });
            }

            prop.InitializeDynamicRuntimeState();
            return prop;
        }

        /// <summary>
        /// Put a prop at rest, which is what <c>Box.checkShelf</c> requires of a support
        /// (<c>Box.as:1198</c>). Set directly rather than by simulating a landing, so a failing test
        /// names the rule and not the landing simulation.
        /// </summary>
        static void AtRest(ObjectInstance prop)
        {
            prop.runtimeState.dynamicState.isGrounded = true;
            prop.runtimeState.dynamicState.stay = true;
        }

        /// <summary>
        /// A room with an empty tile grid, so <c>HasCollision</c> answers "no tile here" and the tests
        /// measure the prop rules rather than the tile data.
        /// </summary>
        static RoomInstance MakeEmptyRoom()
        {
            var room = new RoomInstance
            {
                id = "test_room_0_0",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                landPosition = new Vector3Int(0, 0, 0),
            };

            room.InitializeTiles();
            return room;
        }
    }
}
