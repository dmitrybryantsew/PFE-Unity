using NUnit.Framework;
using PFE.Sim.Campaign;

namespace PFE.Tests.EditMode.Systems.Campaign
{
    /// <summary>
    /// Pins the checkpoint decision rules to <c>fe.loc.CheckPoint</c> (<c>CheckPoint.as:184-291</c>).
    ///
    /// <para><b>Why these rules and not "it activates".</b> <c>CheckPoint.activate</c> is the only place
    /// the game saves (<c>CheckPoint.as:247</c>), so a wrong branch here is a save at the wrong moment —
    /// and a save that happens at the wrong moment looks exactly like one that happened at the right one
    /// until it is loaded. Every case below names the oracle line it pins, and the guard tests carry
    /// <b>both</b> a refused and an accepted case so a "refuse everything" regression cannot pass by
    /// satisfying the negatives alone.</para>
    /// </summary>
    [TestFixture]
    public sealed class CheckpointRulesTests
    {
        private static CheckpointFacts Facts(
            int active = 0,
            bool locked = false,
            bool isBegin = false,
            bool main = false,
            bool teleOn = false,
            bool used = false,
            bool mReturn = false,
            string code = null)
        {
            return new CheckpointFacts
            {
                active = active,
                locked = locked,
                isBegin = isBegin,
                main = main,
                teleOn = teleOn,
                used = used,
                mReturn = mReturn,
                code = code,
            };
        }

        // ---- the opening guard (CheckPoint.as:186-190) ----

        [Test]
        public void Activate_FreshCheckpoint_Activates()
        {
            CheckpointActivation result = CheckpointRules.Activate(Facts(active: CheckpointRules.Fresh));

            Assert.IsFalse(result.refused, "a fresh, unlocked checkpoint must activate");
            Assert.AreEqual(CheckpointRules.Activated, result.newActive,
                "CheckPoint.as:203 sets active = 2");
        }

        [Test]
        public void Activate_AlreadyActivated_IsRefused()
        {
            CheckpointActivation result = CheckpointRules.Activate(Facts(active: CheckpointRules.Activated));

            Assert.IsTrue(result.refused, "CheckPoint.as:190 — `if(this.active == 2) return;`");
            Assert.AreEqual(CheckpointRules.Activated, result.newActive,
                "a refusal must not change the state it refused");
        }

        [Test]
        public void Activate_Locked_IsRefused()
        {
            CheckpointActivation result = CheckpointRules.Activate(Facts(locked: true));

            Assert.IsTrue(result.refused, "CheckPoint.as:186-189 — lock/mine guard");
            Assert.AreEqual(CheckpointRules.Fresh, result.newActive);
        }

        [Test]
        public void Activate_ReopenableCheckpoint_ActivatesAgain()
        {
            // active == 1 is the reopenable state (CheckPoint.as:280-289 deactivate()); it is NOT refused.
            // Without this case a guard written as `active != 0` would pass both tests above and be wrong.
            CheckpointActivation result = CheckpointRules.Activate(Facts(active: CheckpointRules.Reopenable));

            Assert.IsFalse(result.refused, "only active == 2 is refused, not active == 1");
            Assert.AreEqual(CheckpointRules.Activated, result.newActive);
        }

        // ---- the constructor normalisation (CheckPoint.as:113-118, :129-135) ----

        [Test]
        public void Construct_MainCheckpoint_ForcesActiveToActivated()
        {
            CheckpointFacts built = CheckpointRules.Construct(Facts(active: CheckpointRules.Fresh, main: true));

            Assert.AreEqual(CheckpointRules.Activated, built.active,
                "CheckPoint.as:133 — a main checkpoint is constructed at active = 2");
        }

        [Test]
        public void Construct_NonMainCheckpoint_LeavesActiveAlone()
        {
            // Control: Construct is not "always activate". Without this a Construct that unconditionally
            // returned active = 2 would satisfy the case above and still be wrong.
            Assert.AreEqual(CheckpointRules.Fresh,
                CheckpointRules.Construct(Facts(active: CheckpointRules.Fresh, main: false)).active);
            Assert.AreEqual(CheckpointRules.Reopenable,
                CheckpointRules.Construct(Facts(active: CheckpointRules.Reopenable, main: false)).active);
        }

        [Test]
        public void Activate_MainCheckpoint_IsRefused()
        {
            // The whole reason Construct exists. AS3's activate() has no `main` term (CheckPoint.as:186-190),
            // so a main checkpoint is refused only because its constructor left it at active == 2. Before
            // Construct was applied this path healed, granted the XP bonus and SAVED — none of which the
            // oracle does, because its button runs `teleport` instead (:136).
            CheckpointActivation result = CheckpointRules.Activate(
                CheckpointRules.Construct(Facts(active: CheckpointRules.Fresh, main: true, code: "cp_main")));

            Assert.IsTrue(result.refused, "CheckPoint.as:190 — `if(this.active == 2) return;`");
            Assert.IsFalse(result.grantsRestoreBonus, "a refused activation must not heal or grant XP");
            Assert.IsFalse(result.writesCode, "a refused activation must not write lastCpCode");
        }

        [Test]
        public void AreaActivates_MainCheckpoint_NeverFires()
        {
            // CheckPoint.as:132 — the constructor sets `this.area = null`, so the touch path is unreachable
            // for a main checkpoint; here that is the normalised active == 2.
            Assert.IsFalse(CheckpointRules.AreaActivates(
                CheckpointRules.Construct(Facts(active: CheckpointRules.Fresh, main: true))));
        }

        // ---- the restore bonus (CheckPoint.as:191-201) ----

        [Test]
        public void Activate_FreshNonBeginCheckpoint_GrantsRestoreBonus()
        {
            CheckpointActivation result = CheckpointRules.Activate(Facts(active: CheckpointRules.Fresh));

            Assert.IsTrue(result.grantsRestoreBonus,
                "CheckPoint.as:191 — `if(this.active == 0 && param1 == false)`");
        }

        [Test]
        public void Activate_BeginCheckpoint_SkipsRestoreBonus()
        {
            CheckpointActivation result = CheckpointRules.Activate(
                Facts(active: CheckpointRules.Fresh, isBegin: true));

            Assert.IsFalse(result.grantsRestoreBonus,
                "param1 (isBegin) suppresses the mana/XP restore");
        }

        [Test]
        public void Activate_ReopenableCheckpoint_SkipsRestoreBonus()
        {
            CheckpointActivation result = CheckpointRules.Activate(Facts(active: CheckpointRules.Reopenable));

            Assert.IsFalse(result.grantsRestoreBonus,
                "the bonus is gated on active == 0, so a reopen does not re-grant it");
        }

        // ---- the code write (CheckPoint.as:206-212) ----

        [Test]
        public void Activate_WithCode_WritesTheCode()
        {
            CheckpointActivation result = CheckpointRules.Activate(Facts(code: "cp_camp_1"));

            Assert.IsTrue(result.writesCode, "CheckPoint.as:210 — `if(code)` writes prevCPCode + lastCpCode");
        }

        [Test]
        public void Activate_EmptyCode_DoesNotWriteTheCode()
        {
            // The exit room's checkpoint authors no code (RoomsProb.as:4724 `x='29' y='15' tele='1'`), so
            // this is the common case, not an edge case. It must leave lastCpCode alone rather than
            // clearing it.
            CheckpointActivation withNull = CheckpointRules.Activate(Facts(code: null));
            CheckpointActivation withEmpty = CheckpointRules.Activate(Facts(code: string.Empty));

            Assert.IsFalse(withNull.writesCode, "a null code is not written");
            Assert.IsFalse(withEmpty.writesCode, "an empty code is not written");
        }

        // ---- the teleport offer (CheckPoint.as:225-235) ----

        [Test]
        public void Activate_TeleportOffer_RequiresAllThreeConditions()
        {
            CheckpointActivation all = CheckpointRules.Activate(Facts(teleOn: true, mReturn: true, used: false));

            Assert.IsTrue(all.offersTeleport, "mReturn && teleOn && !used must offer the teleport");

            Assert.IsFalse(CheckpointRules.Activate(Facts(teleOn: true, mReturn: false, used: false)).offersTeleport,
                "no mReturn — no offer");
            Assert.IsFalse(CheckpointRules.Activate(Facts(teleOn: false, mReturn: true, used: false)).offersTeleport,
                "no tele attribute — no offer");
            Assert.IsFalse(CheckpointRules.Activate(Facts(teleOn: true, mReturn: true, used: true)).offersTeleport,
                "a spent one-shot (used) — no offer");
        }

        // ---- areaActivate (CheckPoint.as:274-279) ----

        [Test]
        public void AreaActivates_OnlyWhenFresh()
        {
            Assert.IsTrue(CheckpointRules.AreaActivates(Facts(active: CheckpointRules.Fresh)));
            Assert.IsFalse(CheckpointRules.AreaActivates(Facts(active: CheckpointRules.Reopenable)));
            Assert.IsFalse(CheckpointRules.AreaActivates(Facts(active: CheckpointRules.Activated)));
        }

        // ---- deactivate (CheckPoint.as:280-289, :320-322) ----

        [Test]
        public void Deactivates_ReopensANonMainCheckpointThatIsNoLongerCurrent()
        {
            Assert.IsTrue(CheckpointRules.Deactivates(CheckpointRules.Activated, main: false, isCurrentCheckpoint: false),
                "CheckPoint.as:320-322 — active == 2 && pers.currentCP != this");
        }

        [Test]
        public void Deactivates_RefusesTheMainCheckpoint()
        {
            Assert.IsFalse(CheckpointRules.Deactivates(CheckpointRules.Activated, main: true, isCurrentCheckpoint: false),
                "CheckPoint.as:282-285 — `if(this.main) return;`");
        }

        [Test]
        public void Deactivates_RefusesTheCurrentCheckpoint()
        {
            Assert.IsFalse(CheckpointRules.Deactivates(CheckpointRules.Activated, main: false, isCurrentCheckpoint: true),
                "the checkpoint the player is standing at stays activated");
        }

        [Test]
        public void Deactivates_RefusesANonActivatedCheckpoint()
        {
            Assert.IsFalse(CheckpointRules.Deactivates(CheckpointRules.Fresh, main: false, isCurrentCheckpoint: false));
            Assert.IsFalse(CheckpointRules.Deactivates(CheckpointRules.Reopenable, main: false, isCurrentCheckpoint: false));
        }

        // ---- teleport target (CheckPoint.as:250-267) ----

        [Test]
        public void TeleportTarget_NonMain_GoesToTheHub()
        {
            Assert.AreEqual("rbl", CheckpointRules.TeleportTarget(main: false, missionId: "random_plant", baseId: "rbl"),
                "CheckPoint.as:252 — `gotoLand(game.baseId)`");
        }

        [Test]
        public void TeleportTarget_Main_GoesBackToTheMission()
        {
            Assert.AreEqual("nio", CheckpointRules.TeleportTarget(main: true, missionId: "nio", baseId: "rbl"),
                "CheckPoint.as:263 — `gotoLand(game.missionId)`");
        }

        [Test]
        public void TeleportTarget_Main_IsANoOpWhenTheMissionIsTheHub()
        {
            // AS3 spells this `else if(game.missionId != "rbl")`; "" is the honest answer — teleporting
            // to the camp you are already in is nothing at all, not a transition.
            Assert.AreEqual(string.Empty, CheckpointRules.TeleportTarget(main: true, missionId: "rbl", baseId: "rbl"));
            Assert.AreEqual(string.Empty, CheckpointRules.TeleportTarget(main: true, missionId: null, baseId: "rbl"));
        }

        // ---- the locked-variant id roll (Location.as:2094-2098) ----

        /// <summary>
        /// <c>if(!param1 &amp;&amp; this.land.rnd &amp;&amp; Math.random() &lt; 0.5)</c> — all three conditions, and
        /// the begin checkpoint short-circuits all of them.
        /// </summary>
        [Test]
        public void SelectObjectId_RollsAVariantOnlyWhenAllThreeConditionsHold()
        {
            Assert.AreEqual("checkpoint1",
                CheckpointRules.SelectObjectId(isBegin: false, landIsRandom: true, variantRollPassed: true, variantRoll: 1),
                "a non-begin checkpoint on a random land whose roll passed takes the suffix");

            Assert.AreEqual(CheckpointRules.PlainCheckpointId,
                CheckpointRules.SelectObjectId(isBegin: true, landIsRandom: true, variantRollPassed: true, variantRoll: 3),
                "param1 — the begin checkpoint never rolls, so the player's first save point is never locked");

            Assert.AreEqual(CheckpointRules.PlainCheckpointId,
                CheckpointRules.SelectObjectId(isBegin: false, landIsRandom: false, variantRollPassed: true, variantRoll: 3),
                "land.rnd — an authored land never rolls");

            Assert.AreEqual(CheckpointRules.PlainCheckpointId,
                CheckpointRules.SelectObjectId(isBegin: false, landIsRandom: true, variantRollPassed: false, variantRoll: 3),
                "the 50% roll — a failed roll leaves the plain id");
        }

        [Test]
        public void SelectObjectId_NamesAllFiveAuthoredVariants()
        {
            for (int variant = 1; variant <= CheckpointRules.LockedVariantCount; variant++)
            {
                Assert.AreEqual(
                    CheckpointRules.PlainCheckpointId + variant,
                    CheckpointRules.SelectObjectId(false, true, true, variant),
                    $"AllData.as:5008-5012 authors checkpoint{variant}");
            }
        }

        /// <summary>
        /// The draw is <c>Math.floor(Math.random() * 5 + 1)</c>, so 1..5 is the reachable range and the
        /// clamp is only a guard against a caller passing something else. A clamp that let 0 through
        /// would name <c>checkpoint0</c>, which no definition row matches.
        /// </summary>
        [Test]
        public void SelectObjectId_ClampsToTheAuthoredRange()
        {
            Assert.AreEqual("checkpoint1", CheckpointRules.SelectObjectId(false, true, true, 0));
            Assert.AreEqual("checkpoint1", CheckpointRules.SelectObjectId(false, true, true, -4));
            Assert.AreEqual("checkpoint5", CheckpointRules.SelectObjectId(false, true, true, 9));
        }

        /// <summary>
        /// Control: the roll is a roll, not a constant. If <see cref="CheckpointRules.SelectObjectId"/>
        /// returned the plain id unconditionally the first two asserts below would still pass.
        /// </summary>
        [Test]
        public void SelectObjectId_IsNotAConstant()
        {
            string rolled = CheckpointRules.SelectObjectId(false, true, true, 4);
            string plain = CheckpointRules.SelectObjectId(false, true, false, 4);

            Assert.AreNotEqual(rolled, plain, "the roll must be able to change the id");
        }

        // ---- the padlock graphic (CheckPoint.as:62-76) ----

        [Test]
        public void LockVariantNumber_ReadsTheEleventhCharacter()
        {
            Assert.AreEqual(1, CheckpointRules.LockVariantNumber("checkpoint1"));
            Assert.AreEqual(5, CheckpointRules.LockVariantNumber("checkpoint5"));

            Assert.AreEqual(0, CheckpointRules.LockVariantNumber(CheckpointRules.PlainCheckpointId),
                "`checkpoint` is ten characters, so charAt(10) is \"\" — falsy, and AS3 hides the padlock");
        }

        /// <summary>
        /// AS3 sets <c>this.locked = true</c> only <i>after</i> <c>vis.lock.gotoAndStop(n)</c> returns,
        /// and <c>gotoAndStop</c> throws on a frame the clip lacks — caught by the <c>try/catch</c> that
        /// wraps the whole block (<c>CheckPoint.as:62-76</c>). The clip has five frames, so anything
        /// else leaves <c>locked</c> at its <c>false</c> initialiser. Reading a bad digit as "locked"
        /// would be the one direction that cannot be undone at runtime.
        /// </summary>
        [Test]
        public void LockVariantNumber_RefusesDigitsOutsideTheClipsRange()
        {
            Assert.AreEqual(0, CheckpointRules.LockVariantNumber("checkpoint6"));
            Assert.AreEqual(0, CheckpointRules.LockVariantNumber("checkpoint9"));
            Assert.AreEqual(0, CheckpointRules.LockVariantNumber("checkpointX"));
            Assert.AreEqual(0, CheckpointRules.LockVariantNumber(null));
            Assert.AreEqual(0, CheckpointRules.LockVariantNumber(string.Empty));
        }

        /// <summary>
        /// Control for the two cases above: all five authored variants must read as padlocked, so a
        /// <c>LockVariantNumber</c> that returned 0 unconditionally cannot pass them.
        /// </summary>
        [Test]
        public void IsLockedVariant_IsTrueForEveryAuthoredVariantAndFalseForThePlainOne()
        {
            for (int variant = 1; variant <= CheckpointRules.LockedVariantCount; variant++)
            {
                Assert.IsTrue(CheckpointRules.IsLockedVariant(CheckpointRules.PlainCheckpointId + variant),
                    $"checkpoint{variant} draws with a padlock (CheckPoint.as:64-68)");
            }

            Assert.IsFalse(CheckpointRules.IsLockedVariant(CheckpointRules.PlainCheckpointId),
                "the plain checkpoint hides the padlock");
        }

        // ---- the lock/mine gate (CheckPoint.as:186-189) ----

        /// <summary>
        /// The five definition rows, verbatim from <c>AllData.as:5007-5012</c>. The plain
        /// <c>checkpoint</c> authors neither attribute; <c>checkpoint1</c>/<c>2</c> carry
        /// <c>lock='1.4'</c>, <c>checkpoint3</c> <c>lock='2'</c>, and <c>checkpoint4</c>/<c>5</c>
        /// <c>mine='1'</c>.
        /// </summary>
        [Test]
        public void IsLocked_MatchesTheFiveAuthoredDefinitionRows()
        {
            Assert.IsFalse(CheckpointRules.IsLocked("", ""), "plain checkpoint — neither attribute");
            Assert.IsTrue(CheckpointRules.IsLocked("1.4", ""), "checkpoint1/2 — lock='1.4'");
            Assert.IsTrue(CheckpointRules.IsLocked("2", ""), "checkpoint3 — lock='2'");
            Assert.IsTrue(CheckpointRules.IsLocked("", "1"), "checkpoint4/5 — mine='1'");
        }

        /// <summary>
        /// A present-but-zero lock is <b>not</b> locked: the oracle tests <c>inter.lock &gt; 0</c>, not
        /// "has a lock attribute". AS3 relies on this — <c>Interact</c> clears <c>mine</c> when it reads
        /// <c>@lock == "0"</c> (<c>Interact.as:341-343</c>), which is the state a picked lock leaves.
        /// </summary>
        [Test]
        public void IsLocked_TreatsZeroAsUnlocked()
        {
            Assert.IsFalse(CheckpointRules.IsLocked("0", ""), "`lock > 0` — zero is open");
            Assert.IsFalse(CheckpointRules.IsLocked("0.0", "0"), "both zero is open");
            Assert.IsFalse(CheckpointRules.IsLocked(null, null));
            Assert.IsFalse(CheckpointRules.IsLocked("nonsense", ""), "an unparseable value is not a lock");
        }

        /// <summary>
        /// Control: the gate is not a constant in either direction.
        /// </summary>
        [Test]
        public void IsLocked_IsNotAConstant()
        {
            Assert.IsTrue(CheckpointRules.IsLocked("1.4", ""));
            Assert.IsFalse(CheckpointRules.IsLocked("", ""));
        }

        // ---- the walk-into area box (CheckPoint.as:48-53, :90-92) ----

        /// <summary>
        /// The checkpoint's footprint is <c>size='2' wid='3'</c> (<c>AllData.as:5007</c>) — <b>2 tiles
        /// wide by 3 tall</b>. <c>WorldConstants.TILE_SIZE</c> is 40 source pixels and the port draws at
        /// 100 pixels per unit, so a tile is 0.4 world units and the box is 0.8 x 1.2, bottom-anchored.
        /// </summary>
        [Test]
        public void ResolveAreaBox_UsesTheCheckpointsFootprint()
        {
            CheckpointRules.CheckpointAreaBox box = CheckpointRules.ResolveAreaBox(
                sizeTiles: 2, widTiles: 3, tileSizeWorld: 0.4f);

            Assert.AreEqual(0.4f, box.halfWidth, 1e-5f, "scX/2 = (2 * 0.4) / 2");
            Assert.AreEqual(1.2f, box.height, 1e-5f, "scY = 3 * 0.4");
            Assert.AreEqual(0.6f, box.centreYOffset, 1e-5f,
                "`Y1 = Y - scY`, `Y2 = Y` — bottom-anchored, so the centre is half the height up");
        }

        /// <summary>
        /// <b>Control, and the one that matters:</b> <c>size</c> is the <i>width</i> and <c>wid</c> is
        /// the <i>height</i> — the oracle's names read backwards. Swapping them still produces a box of
        /// the same area, so only an asymmetric case can tell the two apart.
        /// </summary>
        [Test]
        public void ResolveAreaBox_DoesNotSwapSizeAndWid()
        {
            CheckpointRules.CheckpointAreaBox box = CheckpointRules.ResolveAreaBox(
                sizeTiles: 3, widTiles: 2, tileSizeWorld: 0.4f);

            Assert.AreEqual(0.6f, box.halfWidth, 1e-5f, "size=3 is the width");
            Assert.AreEqual(0.8f, box.height, 1e-5f, "wid=2 is the height");
        }

        [Test]
        public void ResolveAreaBox_DegenerateFootprintCollapsesRatherThanInverting()
        {
            CheckpointRules.CheckpointAreaBox box = CheckpointRules.ResolveAreaBox(0, -3, 0.4f);

            Assert.AreEqual(0f, box.halfWidth, 1e-5f);
            Assert.AreEqual(0f, box.height, 1e-5f);
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // Land-entry resume (AS3 Land.enterLand / Game.as:347-350)
        // ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>_loc1_</c> is true for exactly one combination. All four are asserted: the failure this
        /// guards against is not "the term is missing" but "the term is inverted", which a single
        /// true-case assertion cannot see.
        /// </summary>
        [Test]
        public void IsFirstVisit_IsTrueOnlyForAnUnvisitedAuthoredLand()
        {
            Assert.IsTrue(CheckpointRules.IsFirstVisit(landIsRandom: false, visited: false),
                "an authored land entered for the first time");

            Assert.IsFalse(CheckpointRules.IsFirstVisit(landIsRandom: false, visited: true),
                "an authored land re-entered");

            Assert.IsFalse(CheckpointRules.IsFirstVisit(landIsRandom: true, visited: false),
                "a procedural land is never a first visit — its layout is rebuilt each entry");

            Assert.IsFalse(CheckpointRules.IsFirstVisit(landIsRandom: true, visited: true),
                "a procedural land, already entered");
        }

        [Test]
        public void IsFirstVisit_IsNotAConstant()
        {
            bool someTrue = CheckpointRules.IsFirstVisit(false, false);
            bool someFalse = !CheckpointRules.IsFirstVisit(false, true);

            Assert.IsTrue(someTrue && someFalse,
                "the predicate must both hold and fail, or it is not a predicate");
        }

        /// <summary>
        /// The room is the checkpoint's own, not the origin. The values are deliberately not (0,0,0):
        /// a rule that returned <c>default</c> would satisfy an assertion written against the origin.
        /// </summary>
        [Test]
        public void ResumeRoomOnEntry_ReturnsTheCheckpointsOwnRoom()
        {
            CheckpointRules.CheckpointRoom? room = CheckpointRules.ResumeRoomOnEntry(
                hasCheckpoint: true, checkpointLandId: "plant", targetLand: "plant",
                firstVisit: false, roomX: 7, roomY: 3, roomZ: 1);

            Assert.IsTrue(room.HasValue, "a re-visit to the checkpoint's own land must resume");

            Assert.AreEqual(7, room.Value.x);
            Assert.AreEqual(3, room.Value.y);
            Assert.AreEqual(1, room.Value.z);
        }

        /// <summary>AS3's <c>!param1</c> term — the first visit goes to the begin cell instead.</summary>
        [Test]
        public void ResumeRoomOnEntry_RefusesOnAFirstVisit()
        {
            CheckpointRules.CheckpointRoom? room = CheckpointRules.ResumeRoomOnEntry(
                hasCheckpoint: true, checkpointLandId: "plant", targetLand: "plant",
                firstVisit: true, roomX: 7, roomY: 3, roomZ: 1);

            Assert.IsNull(room, "a first visit must enter at the begin cell, not the checkpoint");
        }

        /// <summary>
        /// <c>this.currentCP</c> is the <i>land's</i> checkpoint, so one left in another land must not
        /// hijack this entry. Asserted against the same coordinates that succeed above, so the only
        /// difference is the land id.
        /// </summary>
        [Test]
        public void ResumeRoomOnEntry_RefusesACheckpointLeftInAnotherLand()
        {
            CheckpointRules.CheckpointRoom? room = CheckpointRules.ResumeRoomOnEntry(
                hasCheckpoint: true, checkpointLandId: "mbase", targetLand: "plant",
                firstVisit: false, roomX: 7, roomY: 3, roomZ: 1);

            Assert.IsNull(room, "a checkpoint in another land must not select this land's room");
        }

        [Test]
        public void ResumeRoomOnEntry_MatchesTheLandIdCaseInsensitively()
        {
            CheckpointRules.CheckpointRoom? room = CheckpointRules.ResumeRoomOnEntry(
                hasCheckpoint: true, checkpointLandId: "Plant", targetLand: "plant",
                firstVisit: false, roomX: 7, roomY: 3, roomZ: 1);

            Assert.IsTrue(room.HasValue, "land ids are compared case-insensitively elsewhere in the port");
        }

        [Test]
        public void ResumeRoomOnEntry_RefusesWithoutACheckpointOrWithoutALandId()
        {
            Assert.IsNull(CheckpointRules.ResumeRoomOnEntry(
                    false, "plant", "plant", false, 7, 3, 1),
                "no checkpoint recorded");

            Assert.IsNull(CheckpointRules.ResumeRoomOnEntry(
                    true, "", "plant", false, 7, 3, 1),
                "a checkpoint with no land id cannot be matched to one");

            Assert.IsNull(CheckpointRules.ResumeRoomOnEntry(
                    true, "plant", "", false, 7, 3, 1),
                "a nameless target land cannot be matched to a checkpoint");
        }

        /// <summary>
        /// Control: the rule must both refuse and accept. A rule hard-wired to <c>null</c> would make
        /// every "refuses" case above pass, and a rule hard-wired to a room would make the accept case
        /// pass — only holding both proves it is conditional.
        /// </summary>
        [Test]
        public void ResumeRoomOnEntry_IsNotAConstant()
        {
            bool refusedSomething = CheckpointRules.ResumeRoomOnEntry(
                true, "plant", "plant", true, 7, 3, 1) == null;
            bool acceptedSomething = CheckpointRules.ResumeRoomOnEntry(
                true, "plant", "plant", false, 7, 3, 1) != null;

            Assert.IsTrue(refusedSomething && acceptedSomething,
                "the rule must both refuse and accept, or it is not a rule");
        }

        /// <summary>
        /// The precedence, which is the part a <c>??</c> chain hides: a caller that named a room keeps it
        /// even when the land has a checkpoint to resume at.
        /// </summary>
        [Test]
        public void ResolveEntryRoomSource_PrefersTheNamedRoomOverTheCheckpoint()
        {
            Assert.AreEqual(
                CheckpointRules.EntryRoomSource.Named,
                CheckpointRules.ResolveEntryRoomSource(hasNamedRoom: true, hasCheckpointRoom: true),
                "AS3 tests param2 first (Land.as:1146), so a named room beats the checkpoint");
        }

        [Test]
        public void ResolveEntryRoomSource_UsesTheCheckpointOnlyWhenNoRoomWasNamed()
        {
            Assert.AreEqual(
                CheckpointRules.EntryRoomSource.Checkpoint,
                CheckpointRules.ResolveEntryRoomSource(hasNamedRoom: false, hasCheckpointRoom: true));
        }

        [Test]
        public void ResolveEntryRoomSource_FallsBackToTheEntranceWhenBothAreAbsent()
        {
            Assert.AreEqual(
                CheckpointRules.EntryRoomSource.Entrance,
                CheckpointRules.ResolveEntryRoomSource(hasNamedRoom: false, hasCheckpointRoom: false));
        }

        /// <summary>
        /// Control: three distinct answers for three distinct inputs. A selector collapsed to two
        /// outcomes would satisfy two of the three cases above but not all three.
        /// </summary>
        [Test]
        public void ResolveEntryRoomSource_IsNotAConstant()
        {
            CheckpointRules.EntryRoomSource a =
                CheckpointRules.ResolveEntryRoomSource(true, true);
            CheckpointRules.EntryRoomSource b =
                CheckpointRules.ResolveEntryRoomSource(false, true);
            CheckpointRules.EntryRoomSource c =
                CheckpointRules.ResolveEntryRoomSource(false, false);

            Assert.AreNotEqual(a, b, "named and checkpoint must be distinguishable");
            Assert.AreNotEqual(b, c, "checkpoint and entrance must be distinguishable");
            Assert.AreNotEqual(a, c, "named and entrance must be distinguishable");
        }

        /// <summary>
        /// Control: the guard is a guard, not a constant. If either branch were hard-wired the two asserts
        /// below could not both hold.
        /// </summary>
        [Test]
        public void Guard_IsNotAConstant()
        {
            bool refusedSomething = CheckpointRules.Activate(Facts(active: CheckpointRules.Activated)).refused;
            bool acceptedSomething = !CheckpointRules.Activate(Facts(active: CheckpointRules.Fresh)).refused;

            Assert.IsTrue(refusedSomething && acceptedSomething,
                "the guard must both refuse and accept, or it is not a guard");
        }
    }
}
