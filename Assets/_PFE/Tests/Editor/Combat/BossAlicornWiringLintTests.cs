using NUnit.Framework;
using PFE.Systems.Weapons;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the <b>wiring</b> of the boss-alicorn defects that no behavioural fixture can reach: one
    /// bullet and one lightning for the whole fight, no blades in the second phase, a teleport that
    /// landed her in the air, a beam that never appeared, and a shield dome that was never attached.
    ///
    /// <para><b>Why lints and not behavioural tests.</b> Every one of them lives in a
    /// <c>MonoBehaviour</c> — the brain, the boss's weapon loadout, the room spawner. Outside the editor
    /// each needs a <c>GameObject</c>, a room and a component graph, and throws
    /// <c>SecurityException: ECall methods must be packaged into a system module</c> before reaching an
    /// assertion; this is the same reason <see cref="HoofStrikeWiringLintTests"/> exists. The arithmetic
    /// behind them is pinned behaviourally instead — <c>BossAlicornRulesTests</c> for the roll order and
    /// the teleport geometry, <c>ScytheRulesTests</c> for the blade's orbit and its 60-tick contact
    /// delay. What none of those can see is whether the brain ever <i>calls</i> any of it, and that is
    /// exactly what was broken: <c>emit()</c> counted the blades it did not spawn and logged a warning,
    /// and the weapons were never marked automatic.</para>
    ///
    /// <para><b>Every assertion is paired with a positive control</b> from the same extracted body, so a
    /// lint cannot pass because the extractor returned nothing.</para>
    /// </summary>
    [TestFixture]
    public class BossAlicornWiringLintTests
    {
        private const string BrainPath = "Entities/Enemies/Bosses/BossAlicornBrain.cs";
        private const string BossControllerPath = "Entities/Enemies/Bosses/BossController.cs";
        private const string SpawnerPath = "Systems/Map/Rendering/RoomUnitSpawner.cs";

        // ── 1. "One bullet, one lightning": the weapons must be automatic ──────────────────────

        /// <summary>
        /// Every one of the boss's mounts must be marked as having no click edge.
        ///
        /// <para><b>The defect.</b> <c>Weapon.as:337-340</c> sets <c>auto = true</c> for every
        /// non-player owner, so an NPC's trigger never takes the semi-auto debounce. The port models
        /// that as <c>WeaponRuntimeState.AutomaticTrigger</c>, and the boss calls <c>attack()</c> on
        /// every tick of its step-3 beat — which refreshes <c>t_auto</c> to 3 and decays it to 2
        /// forever, so a semi-automatic mount fires <b>exactly once per fight</b>. That is the reported
        /// symptom, on <c>alilight2</c> (rapid 20), <c>aliblade</c> (10) and <c>alipsy2</c> (30).</para>
        ///
        /// <para><b>Why it must be inside the loop.</b> The call is per-mount and idempotent, and the
        /// four mounts are equipped one per iteration. A call placed after the loop — or worse, on the
        /// first mount only — leaves the other three semi-automatic, which is the same bug with a
        /// shorter list of victims.</para>
        /// </summary>
        [Test]
        public void EquipBossWeapons_MarksEveryMountAsAutomatic()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(BossControllerPath), "void EquipBossWeapons()");

            Assert.That(body, Does.Contain("EquipWeapon"),
                "positive control: this has to be EquipBossWeapons' real body");
            Assert.That(body, Does.Contain("MarkAutomaticTrigger"),
                "without this every boss weapon keeps the semi-auto debounce and fires once for the " +
                "whole fight — `if(!param1.player) this.auto = true` (Weapon.as:337-340)");

            int equipped = body.IndexOf("EquipWeapon", System.StringComparison.Ordinal);
            int marked = body.IndexOf("MarkAutomaticTrigger", System.StringComparison.Ordinal);

            Assert.That(marked, Is.GreaterThan(equipped),
                "the mark has to come after the mount exists — a mount is what carries the " +
                "WeaponRuntimeState the flag lives on");
        }

        // ── 2. The teleport: the emitter flag is read before the difficulty upgrade ────────────

        /// <summary>
        /// <c>AdvanceStepOne</c> must teleport <b>before</b> it rolls the beam upgrade.
        ///
        /// <para><b>The defect this guards against is an ordering one, and it is invisible.</b> AS3's
        /// expression is <c>teleport(…, attState == 4 ? 0 : 1)</c> at <c>:415</c> and the
        /// <c>attState = 5</c> upgrade is at <c>:417-420</c>, so an upgraded beat <i>still arrives with
        /// the teleport flash</i>. Moving the upgrade first — the natural thing to do when tidying the
        /// method — silently suppresses the arrival flash on exactly the 20 % of beats that fire the
        /// beam, and nothing else in the port can notice.</para>
        /// </summary>
        [Test]
        public void AdvanceStepOne_TeleportsBeforeTheDifficultyUpgradeRolls()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(BrainPath), "void AdvanceStepOne()");

            Assert.That(body, Does.Contain("RollAttackState"),
                "positive control: this has to be AdvanceStepOne's real body");

            int teleport = body.IndexOf("TeleportToMovePoint", System.StringComparison.Ordinal);
            int upgrade = body.IndexOf("UpgradesToBeam", System.StringComparison.Ordinal);

            Assert.That(teleport, Is.GreaterThanOrEqualTo(0), "step 1 has to teleport at all");
            Assert.That(upgrade, Is.GreaterThanOrEqualTo(0),
                "…and it has to roll the difficulty upgrade, or alimray is unreachable");
            Assert.That(teleport, Is.LessThan(upgrade),
                "`teleport(..., attState == 4 ? 0 : 1)` (:415) precedes `if(globalDif == 4 …) attState " +
                "= 5` (:417): the flash decision reads the state BEFORE the upgrade");
        }

        /// <summary>
        /// …and the flag it passes must be the <b>pre-upgrade</b> attack state's invisibility test, not
        /// a re-read after the upgrade.
        /// </summary>
        /// <remarks>
        /// <c>attState == 4 ? 0 : 1</c> is <c>param3</c>, and only <c>attState == 4</c> arrives silently
        /// (<c>:333-336</c>). Asserting the argument text is deliberate: a re-read after the upgrade
        /// would still be "an invisibility test" and would still compile, but a beat upgraded from 4 to
        /// 5 would then flash where the oracle does not.
        /// </remarks>
        [Test]
        public void AdvanceStepOne_FeedsTheEmitterFlagThePreUpgradeAttackState()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(BrainPath), "void AdvanceStepOne()");

            Assert.That(body, Does.Contain("TeleportToMovePoint(_movePoint, _attState != " +
                                           "BossAlicornRules.InvisibilityAttackState)"),
                "the second argument is `attState == 4 ? 0 : 1` (:415) — the invisibility state alone " +
                "arrives silently");
        }

        // ── 3. The teleport: the flight probe reads ten pixels BELOW the feet ──────────────────

        /// <summary>
        /// <c>TeleportToMovePoint</c> must probe the landing point and hand the answer to
        /// <c>IsFlyingAfterTeleport</c>.
        ///
        /// <para><b>Two ways to get this wrong, both silent.</b> AS3 is
        /// <c>var _loc4_:Tile = loc.getAbsTile(X, Y + 10)</c> then
        /// <c>if(_loc4_.phis || _loc4_.shelf) isFly = false</c> (<c>:323-331</c>). In AS3's y-down space
        /// <c>Y + 10</c> is ten pixels <i>below</i> the feet; the port is y-up, so the same question is
        /// <c>y - 10</c>. Sampling <c>+10</c> asks about the air above her head, and every move point
        /// then reports "nothing below" — she hovers on all seven points, which is the reported "she
        /// has no room to circle after the teleport". Dropping the probe altogether has the same
        /// effect. The mirrored arithmetic itself is pinned in <c>BossAlicornRulesTests</c>; what this
        /// asserts is that the probe exists and is measured from the landing point.</para>
        /// </summary>
        [Test]
        public void TeleportToMovePoint_ProbesTenPixelsBelowTheFeetForSolidOrShelf()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(BrainPath), "void TeleportToMovePoint(int movePoint, bool emitArrival)");

            Assert.That(body, Does.Contain("IsFlyingAfterTeleport"),
                "positive control: this has to be TeleportToMovePoint's real body");

            Assert.That(body, Does.Contain("targetLocal.y - 10f"),
                "`loc.getAbsTile(X, Y + 10)` (:323) is ten pixels BELOW the feet — in the port's y-up " +
                "space that is a MINUS. A +10 probe asks about the air above her head.");
            Assert.That(body, Does.Not.Contain("targetLocal.y + 10f"),
                "the unmirrored sign is the bug this fixture exists for");
            Assert.That(body, Does.Contain("IsSolidOrShelf()"),
                "`if(_loc4_.phis || _loc4_.shelf)` — a catwalk tile is as much a floor as a solid one");
        }

        // ── 4. The blades: emit() must spawn a real bound scythe ──────────────────────────────

        /// <summary>
        /// <c>EmitScythe</c> must build and spawn a unit, not count one.
        ///
        /// <para><b>The defect.</b> The first port of <c>emit()</c> incremented a counter and logged
        /// "scythes are not implemented". So the second phase's six bound blades — <c>UnitScythe</c>,
        /// the entire visible difference between phase 1 and phase 2 — never existed, and the only
        /// trace was a warning nobody read. The user's report was "she does not have her blades on
        /// second phase".</para>
        ///
        /// <para><b>Each identifier below is one of the four things that made the blade real:</b>
        /// <c>ResourcesUnitDefinitionProvider</c> (the oracle's <c>AllData</c> row, needed for
        /// <c>maxhp</c>), <c>SpawnUnitNow</c> (the room actually placing it),
        /// <c>GetComponent&lt;ScytheController&gt;</c> (the spawned object having the blade
        /// behaviour), and <c>BindTo</c> (the ring slot). A spawn that stopped at
        /// <c>SpawnUnitNow</c> would produce an inert collider, which is why the component check is
        /// asserted separately.</para>
        /// </summary>
        [Test]
        public void EmitScythe_SpawnsARealBladeRatherThanCountingOne()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(BrainPath), "void EmitScythe(int bindIndex = -1)");

            Assert.That(body, Does.Contain("ScytheController.ControllerId"),
                "positive control: this has to be EmitScythe's real body");

            Assert.That(body, Does.Contain("SpawnUnitNow"),
                "`createUnit(\"scythe\", …, true)` (:294-307) — the blade has to be placed in the room");
            Assert.That(body, Does.Contain("GetComponent<ScytheController>"),
                "a spawned unit without the controller is an inert collider; the spawner's table has " +
                "to have been updated too, and this is where a missing entry becomes visible");
            Assert.That(body, Does.Contain("BindTo"),
                "`_loc2_.bind = this; maxhp *= 8; bindN = param1` (:300-304) — the bound form is the " +
                "phase-2 ring");
            Assert.That(body, Does.Contain("SetTarget"),
                "a free blade latches its angle at the player (`cel = World.w.gg`, :60)");
        }

        /// <summary>
        /// …and the blade must be stamped with the <b>boss's</b> faction, not the definition's.
        /// </summary>
        /// <remarks>
        /// <c>_loc2_.fraction = fraction</c> (<c>:297</c>) — and <c>scythe.asset</c> authors
        /// <c>fraction: 2</c> (Raider), so the definition's own value is wrong for this fight. A blade
        /// left at Raider would be attackable by the boss's raider allies and would not count as
        /// hostile to them, which is the kind of thing that only shows up when the arena has company.
        /// </remarks>
        [Test]
        public void EmitScythe_StampsTheBossFactionOnTheBlade()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(BrainPath), "void EmitScythe(int bindIndex = -1)");

            Assert.That(body, Does.Contain("fraction = _controller.Faction"),
                "`_loc2_.fraction = fraction` (:297) — the boss's own faction, overriding the " +
                "definition's Raider(2)");
        }

        /// <summary>
        /// The shield break must emit <b>one blade per ring slot</b>, passing the index.
        /// </summary>
        /// <remarks>
        /// <c>while(_loc2_ &lt; 6) emit(_loc2_++)</c> (<c>:727-732</c>). The index is not decoration: it
        /// becomes <c>bindN</c>, which spaces the six blades 60° apart
        /// (<c>ScytheRules.OrbitPhase</c>). Six unindexed emits would stack all six in one place — the
        /// ring would look like a single blade, which is a failure that reads as "only one scythe".
        /// </remarks>
        [Test]
        public void ShieldBreak_EmitsOneBoundBladePerRingSlot()
        {
            string source = SourceLint.ReadStripped(BrainPath);

            Assert.That(source, Does.Contain("ScythesOnShieldBreak"),
                "positive control: the six-blade loop has to still be there");

            int loop = source.IndexOf("ScythesOnShieldBreak", System.StringComparison.Ordinal);
            int call = source.IndexOf("EmitScythe(i)", System.StringComparison.Ordinal);

            Assert.That(call, Is.GreaterThan(loop),
                "the emit inside the loop must pass the loop index — `emit(_loc2_++)` — or all six " +
                "blades share one orbit phase");
        }

        // ── 5. The spawner: the blade's controller has to be reachable ────────────────────────

        /// <summary>
        /// <c>RoomUnitSpawner</c> must map the scythe's controller id to <c>ScytheController</c>.
        ///
        /// <para><b>Why a table entry rather than a data row.</b> <c>Unit.as:694</c> special-cases
        /// <c>id == "scythe"</c> ahead of the XML lookup, so no <c>&lt;obj cl='UnitScythe'/&gt;</c> row
        /// exists and <c>scythe.asset</c> carries an empty <c>controllerId</c>. The spawner's fallback
        /// chain then lands on the unit id "scythe", misses, and builds a brain-less, art-less base
        /// <c>UnitController</c> — a plausible object that does nothing.</para>
        /// </summary>
        [Test]
        public void RoomUnitSpawner_RegistersTheScytheController()
        {
            string source = SourceLint.ReadStripped(SpawnerPath);

            Assert.That(source, Does.Contain("typeof(RobobrainController)"),
                "positive control: this has to be the spawner's controller table");

            Assert.That(source, Does.Contain("[ScytheController.ControllerId] = typeof(ScytheController)"),
                "without this entry emit() spawns a base UnitController and the blades are inert");
        }

        // ── 6. The dome: a one-shot break, not a shield pool ──────────────────────────────────

        /// <summary>
        /// The spawner must build a <c>UnitShieldOverlay</c> for a unit that draws a dome, and the boss
        /// alicorn's dome is <b>not</b> a shield pool — so the gate cannot be
        /// <c>HasSpellShieldOverlay</c> alone.
        /// </summary>
        /// <remarks>
        /// <para><b>The defect this forbids.</b> The gate was `if (controller.HasSpellShieldOverlay)`,
        /// and the boss returns <c>false</c> there because she has no pool at all — <c>shithp</c> is
        /// declared and never assigned in <c>UnitBossAlicorn</c>. So no component was ever attached and
        /// the Super Alicorn has had no dome since the boss was ported, while the flag that says she
        /// <i>does</i> draw one (<c>UsesBossShieldOverlay</c>) had no reader.</para>
        ///
        /// <para>Two flags, two questions: "has a shield pool" and "draws a dome". The gate has to
        /// accept either, or one of the two kinds of dome is silently absent.</para>
        /// </remarks>
        [Test]
        public void RoomUnitSpawner_BuildsADomeForTheBossAsWellAsForAPool()
        {
            string source = SourceLint.ReadStripped(SpawnerPath);

            Assert.That(source, Does.Contain("AddComponent<UnitShieldOverlay>()"),
                "positive control: this has to be the dome-attach site");

            // The OR form specifically, not the bare name. `controller.UsesBossShieldOverlay` is also
            // an argument to `Initialize` a few lines down, so asserting on the name alone passes with
            // the gate mutated back to the pool-only form — measured: that mutant survived the first
            // version of this lint. The `||` is the gate.
            Assert.That(source, Does.Contain("|| controller.UsesBossShieldOverlay"),
                "the boss's dome has no pool, so gating on HasSpellShieldOverlay alone leaves her bare " +
                "for the whole fight");
        }

        /// <summary>
        /// <c>BossAlicornController</c> must claim the boss dome — and must <b>not</b> claim a pool.
        /// </summary>
        /// <remarks>
        /// Returning <c>true</c> from <c>HasSpellShieldOverlay</c> as well would hand the damage pipeline
        /// a shield pool the oracle does not have: <c>shitArmor</c> is only reached inside
        /// <c>if(shithp &gt; 0)</c>, which never holds for this class, so the boss would start absorbing
        /// damage she should take.
        /// </remarks>
        [Test]
        public void BossAlicornController_ClaimsTheDomeButNotAPool()
        {
            // Asserted on the declaration text, not through MethodBody: these are expression-bodied
            // members (`=> true;`), and a brace-matched extractor would silently return the body of
            // whichever method follows — a lint that passes for the wrong reason.
            string source = SourceLint.ReadStripped(BossControllerPath);

            Assert.That(source, Does.Contain("UsesBossShieldOverlay => true"),
                "the alicorn's visShit3 dome must be attached — UnitBossAlicorn.as:123-127");
            Assert.That(source, Does.Not.Contain("UsesBossShieldOverlay => false"),
                "no controller in this file may disclaim the boss dome");

            Assert.That(source, Does.Contain("HasSpellShieldOverlay => false"),
                "she has no shield pool — `shithp` is declared and never assigned, so claiming one " +
                "would give the boss damage absorption the oracle does not have");

            // Positive control for the two above: the file does have a pool-claiming boss, so a
            // blanket "no controller claims a pool" reading would be wrong.
            Assert.That(source, Does.Contain("HasSpellShieldOverlay => true"),
                "BossUltraController does have a real pool — this file is not all one class");
        }

        /// <summary>
        /// The revive path must raise the dome's break — <c>visshit.gotoAndPlay(2)</c> (<c>:712</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Without this the phase transition has no visual at all.</b> The dome stays on screen
        /// through the shatter, the six blades appear out of an intact bubble, and the player has no
        /// cue that the first lethal hit did anything. The rules fixture proves the timeline; this
        /// proves the brain ever starts it.</para>
        ///
        /// <para><b>And it must come after <c>_isShit = false</c></b> — the same order as the oracle's
        /// two statements — so a break raised twice in one tick cannot re-arm the shield state.</para>
        /// </remarks>
        [Test]
        public void TryReviveSecondLife_BreaksTheDome()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(BrainPath), "public override bool TryReviveSecondLife()");

            Assert.That(body, Does.Contain("_isShit = false"),
                "positive control: this has to be the revive path");

            int cleared = body.IndexOf("_isShit = false", System.StringComparison.Ordinal);
            int broken = body.IndexOf("BreakDome()", System.StringComparison.Ordinal);

            Assert.That(broken, Is.GreaterThanOrEqualTo(0),
                "`this.visshit.gotoAndPlay(2)` (:712) — the dome has to shatter, or the phase " +
                "transition is invisible");
            Assert.That(broken, Is.GreaterThan(cleared),
                "the state clears first, then the clip plays — the oracle's own order");
        }

        /// <summary>
        /// <c>BreakDome</c> must resolve the overlay from the <b>children</b>, lazily.
        /// </summary>
        /// <remarks>
        /// <para><b>Two independent ways to miss it, both silent.</b> The overlay is added by
        /// <c>RoomUnitSpawner</c> to the unit's <c>Visual</c> child — so <c>GetComponent</c> on the
        /// brain's own object never finds it — and it is added <i>after</i> <c>controller.Initialize</c>,
        /// so a cache taken in <c>Awake</c> is permanently null. Either mistake leaves the dome intact
        /// with no warning anywhere: <c>_isShit</c> is cleared by the caller, so the fight proceeds
        /// normally and only the animation is missing.</para>
        /// </remarks>
        [Test]
        public void BreakDome_ResolvesTheOverlayFromTheVisualChild()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(BrainPath), "void BreakDome()");

            Assert.That(body, Does.Contain("BreakOnce"),
                "positive control: this has to be BreakDome's real body");

            Assert.That(body, Does.Contain("GetComponentInChildren<UnitShieldOverlay>"),
                "the dome is a component on the Visual CHILD, added after Initialize — " +
                "GetComponent here would never find it and a cached field would be null forever");
        }

        // ── 5. The animated weapon anchor (wPosAlicornBoss) ──────────────────────────────────

        private const string HoldPointPath = "Entities/Enemies/Bosses/AlicornBossHoldPoint.cs";

        /// <summary>
        /// The <b>alicorn's own</b> slice of <c>BossController.cs</c>, from its class declaration to the
        /// next boss class.
        /// </summary>
        /// <remarks>
        /// <b>Why a slice and not <c>SourceLint.MethodBody</c> on the whole file.</b> That file declares
        /// <c>public override void Initialize(UnitDefinition, UnitStats)</c> <b>three times</b> — once
        /// per boss — and <c>MethodBody</c> returns the <i>first</i> match. A lint written against the
        /// unsliced file would read the raider boss's body and report on a class it never mentions,
        /// passing or failing for the wrong reason. The two assertions inside this helper are the guard:
        /// a slice that failed to find the class, or one that ran to the end of the file, fails here
        /// rather than quietly producing an empty string.
        /// </remarks>
        private static string AlicornControllerSource()
        {
            string all = SourceLint.ReadStripped(BossControllerPath);

            int start = all.IndexOf("public class BossAlicornController", System.StringComparison.Ordinal);
            int end = all.IndexOf("public class BossDronController", System.StringComparison.Ordinal);

            Assert.That(start, Is.GreaterThanOrEqualTo(0),
                "the alicorn controller has to exist for any of this to mean anything");
            Assert.That(end, Is.GreaterThan(start),
                "positive control: the slice must end at the next boss, not at the end of the file");

            return all.Substring(start, end - start);
        }

        /// <summary>
        /// <c>Initialize</c> must attach the animated anchor — the port of
        /// <c>this.wPos = BlitAnim.wPosAlicornBoss</c> (<c>UnitBossAlicorn.as:122</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Without it the four weapons sit at the belly</b> — <c>WeaponHoldRule.Base</c>,
        /// <c>Y - scY/2</c> — because a rule that sees only the body cannot express a per-frame table.
        /// The distance is 79 px and 41 px of forward offset; see
        /// <c>AlicornBossWPosRulesTests.TheRuleFallbackIsNotTheAnchor_WhichIsWhyTheSourceExists</c>.</para>
        ///
        /// <para><b>Why here and not in the brain.</b> The anchor belongs to the unit, not to an attack
        /// state: it has to exist on the tick the first mount resolves its hold point, which is before
        /// any brain beat runs.</para>
        /// </remarks>
        [Test]
        public void BossAlicornController_AttachesTheAnimatedAnchor()
        {
            string slice = AlicornControllerSource();
            string body = SourceLint.MethodBody(
                slice, "public override void Initialize(UnitDefinition definition, UnitStats stats)");

            Assert.That(body, Does.Contain("EquipBossWeapons"),
                "positive control: this has to be the alicorn's own Initialize, not another boss's");

            Assert.That(body, Does.Contain("AlicornBossHoldPoint.Attach(this)"),
                "`this.wPos = BlitAnim.wPosAlicornBoss` (:122) — without the attach every weapon " +
                "falls back to WeaponHoldRule.Base and fires from the belly");
        }

        /// <summary>
        /// The controller must name <see cref="WeaponHoldRule.WPosTable"/>, not <c>Base</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>A label, but the right one.</b> The hold-point source wins over the rule, so this
        /// changes no number while the anchor is attached. What it changes is the answer when it is
        /// <i>not</i>: <c>Base</c> would claim this class overrides nothing, while <c>WPosTable</c> names
        /// the table and falls back to the inherited hold — the same place the oracle's own
        /// <c>catch</c> branch lands. See <c>BossAlicornController.ResolveHoldRule</c>.</para>
        /// </remarks>
        [Test]
        public void BossAlicornController_NamesTheWPosTableRule()
        {
            string slice = AlicornControllerSource();

            Assert.That(slice, Does.Contain("UsesBossShieldOverlay"),
                "positive control: this is the alicorn's class slice");

            Assert.That(slice, Does.Contain("ResolveHoldRule() => WeaponHoldRule.WPosTable"),
                "UnitBossAlicorn.setWeaponPos is the wPos-table override (:174-189)");
            Assert.That(slice, Does.Not.Contain("ResolveHoldRule() => WeaponHoldRule.Base"),
                "Base would read as 'this class overrides nothing', which is the one thing it does not");
        }

        /// <summary>
        /// The anchor must read the table <b>and</b> mirror it by the facing — AS3's <c>* storona</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>The mirror is the half that is easy to drop, and the griffin is why.</b>
        /// <c>GriffinArmMath.ArmOffsetPixels</c> deliberately omits <c>* storona</c> because the griffin's
        /// arm is a <i>child</i> of the unit root and inherits <c>localScale.x</c>. This anchor returns a
        /// <b>world point</b>, which inherits nothing — so the same helper is reused and the facing is
        /// applied here. Copying the griffin's call without the multiply puts the weapon 41 px behind her
        /// the moment she turns left, and every fixture stays green because no fixture can construct the
        /// component.</para>
        /// </remarks>
        [Test]
        public void AlicornBossHoldPoint_MirrorsTheAnchorByFacing()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(HoldPointPath), "public bool TryResolveHoldPoint(out Vector2 worldPoint)");

            Assert.That(body, Does.Contain("ArmOffsetPixels"),
                "positive control: this has to be the resolving method");
            Assert.That(body, Does.Contain("AlicornBossWPosRules.TryResolveCell"),
                "the anchor comes from the table, not from a constant");

            Assert.That(body, Does.Contain("offset.x * facing"),
                "`weaponX = X + (obj.x + visBmp.x) * storona` (:181) — a world point inherits no " +
                "mirror from the unit root, so it has to be applied here");
        }

        /// <summary>
        /// The animator must be found through the <b>children</b>, lazily.
        /// </summary>
        /// <remarks>
        /// The body's <c>UnitAnimator</c> lives on the <c>Visual</c> child that
        /// <c>RoomUnitSpawner</c> builds, and the anchor is attached from <c>Initialize</c>, which runs
        /// before that child is guaranteed to exist. <c>GetComponent</c> on the root never finds it;
        /// a field captured once would be null forever. Either mistake makes
        /// <c>TryResolveHoldPoint</c> answer <c>false</c> for the whole fight, which is silent — the
        /// weapon simply stays at the fallback.
        /// </remarks>
        [Test]
        public void AlicornBossHoldPoint_ResolvesTheAnimatorFromTheChildren()
        {
            string source = SourceLint.ReadStripped(HoldPointPath);

            Assert.That(source, Does.Contain("TryResolveCell"),
                "positive control: the file has to be the anchor");

            Assert.That(source, Does.Contain("GetComponentInChildren<UnitAnimator>(true)"),
                "the animator is on the Visual CHILD, built after Initialize — GetComponent on the " +
                "root finds nothing and a captured field stays null");
        }
    }
}
