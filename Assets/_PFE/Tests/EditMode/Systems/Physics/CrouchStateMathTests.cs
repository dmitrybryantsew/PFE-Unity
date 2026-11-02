using System.IO;
using NUnit.Framework;
using PFE.Systems.Physics;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.EditMode.Systems.Physics
{
    /// <summary>
    /// Pins AS3's <c>isSit</c> — the crouch <b>latch</b> — and the roll that only its first frames play.
    ///
    /// <para><b>The bug these exist for.</b> The port shipped crouch as a key level
    /// (<c>_isCrouching = grounded &amp;&amp; … &amp;&amp; move.y &lt; -0.5</c>), so releasing Down stood the
    /// player up and the crouch never outlived the key. The oracle latches: <c>sit(true)</c> once
    /// (<c>UnitPlayer.as:2859</c>), cleared only by Up (<c>:2884</c>) or by leaving the ground
    /// (<c>:2889</c>). Because a level and a latch agree for exactly one frame, no assertion on a
    /// single frame can tell them apart — <c>ShouldEndCrouch_ReleasingDownDoesNotStandYouUp</c> is the
    /// one that does, and it is the reported behaviour verbatim.</para>
    ///
    /// <para><b>The roll is an entry-frame transition, not a state.</b> <c>maxSpeed</c> is
    /// <c>sitSpeed</c> on every frame the player is already sitting, so the oracle's
    /// <c>maxSpeed &gt; walkSpeed * 1.6</c> can only pass on the frame the crouch begins. These
    /// fixtures pin that from both sides: the run speed rolls, the sit speed (half the walk) does
    /// not — so a future edit that feeds the <i>current</i> speed into <c>ShouldRoll</c> makes the roll
    /// permanently unreachable, and a future edit that drops the gate makes every crouch roll.</para>
    /// </summary>
    [TestFixture]
    public class CrouchStateMathTests
    {
        /// <summary>littlepip's walk speed, <c>AllData.as:22</c> <c>&lt;move speed='7'/&gt;</c>.</summary>
        private const float WalkSpeed = 7f;

        /// <summary>AS3 <c>runSpeed = walkSpeed * 2</c> (<c>UnitPlayer.as:356</c>).</summary>
        private const float RunSpeed = WalkSpeed * 2f;

        /// <summary>AS3 <c>sitSpeed = walkSpeed * 0.5</c> (<c>UnitPlayer.as:354</c>).</summary>
        private const float SitSpeed = WalkSpeed * 0.5f;

        /// <summary><c>SimClock.FramesPerSecond</c> — the canonical 30, not a live tick rate.</summary>
        private const float CanonicalFps = 30f;

        // ── The keys ─────────────────────────────────────────────────────────────────────────────

        [Test]
        public void SitHeld_StartsExactlyAtTheDownDeadzone()
        {
            Assert.IsTrue(CrouchStateMath.SitHeld(-1f));
            Assert.IsTrue(CrouchStateMath.SitHeld(-0.51f));
            Assert.IsFalse(CrouchStateMath.SitHeld(-0.5f), "the deadzone edge is not 'held'");
            Assert.IsFalse(CrouchStateMath.SitHeld(0f));
        }

        [Test]
        public void UpHeld_StartsExactlyAtTheUpDeadzone_AndARestingStickIsNeither()
        {
            Assert.IsTrue(CrouchStateMath.UpHeld(0.51f));
            Assert.IsFalse(CrouchStateMath.UpHeld(0.5f));
            Assert.IsFalse(CrouchStateMath.UpHeld(0f));

            Assert.IsFalse(CrouchStateMath.SitHeld(0f), "a resting stick must not read as Down");
            Assert.IsFalse(CrouchStateMath.UpHeld(0f), "a resting stick must not read as Up");
        }

        // ── The hold debounce (AS3 `downp`) ──────────────────────────────────────────────────────

        [Test]
        public void RequiredHold_IsTwoFramesOnSolidGround_AndSixOnAShelf()
        {
            // AS3 UnitPlayer.as:2859 — `stayPhis == 1 && downp == 1 || stayPhis == 2 && downp == 5`,
            // with `downp` incremented after the test, so each threshold is one frame later.
            Assert.That(CrouchStateMath.RequiredHoldFrames(onShelf: false), Is.EqualTo(2));
            Assert.That(CrouchStateMath.RequiredHoldFrames(onShelf: true), Is.EqualTo(6));
        }

        [Test]
        public void RequiredHoldSeconds_IsTheOracleDuration_NotOneFixedStep()
        {
            Assert.That(CrouchStateMath.RequiredHoldSeconds(false, CanonicalFps),
                Is.EqualTo(2f / 30f).Within(1e-6f));
            Assert.That(CrouchStateMath.RequiredHoldSeconds(true, CanonicalFps),
                Is.EqualTo(6f / 30f).Within(1e-6f));

            // 200 ms is the shelf debounce — long enough for a double-tap to be recognised as a
            // drop-through first, short enough that a deliberate hold still crouches promptly.
            Assert.That(CrouchStateMath.RequiredHoldSeconds(true, CanonicalFps),
                Is.EqualTo(0.2f).Within(1e-6f));

            // A degenerate rate must not divide by zero and produce Infinity, which would compare
            // false against every elapsed time and silently disable the crouch.
            Assert.That(CrouchStateMath.RequiredHoldSeconds(false, 0f), Is.EqualTo(0f));
        }

        // ── Entry ────────────────────────────────────────────────────────────────────────────────

        [Test]
        public void MayBeginCrouch_RequiresGround_AndRefusesWaterLadderAndForcedRun()
        {
            Assert.IsTrue(CrouchStateMath.MayBeginCrouch(
                grounded: true, inWater: false, onLadder: false, runForced: false));

            Assert.IsFalse(CrouchStateMath.MayBeginCrouch(true, false, false, true),
                "AS3 :2839 — `runForever <= 0`: the horror effect refuses the crouch");
            Assert.IsFalse(CrouchStateMath.MayBeginCrouch(false, false, false, false),
                "AS3 :2839 — `stay`: no crouching in mid-air");
            Assert.IsFalse(CrouchStateMath.MayBeginCrouch(true, true, false, false),
                "AS3 :2839 — `!inWater`: Down swims down instead");
            Assert.IsFalse(CrouchStateMath.MayBeginCrouch(true, false, true, false),
                "AS3 :2862-2867 — on a ladder Down descends instead; the two cannot both be taken");
        }

        // ── Release: the latch ───────────────────────────────────────────────────────────────────

        [Test]
        public void ShouldEndCrouch_ReleasingDownDoesNotStandYouUp()
        {
            // THE reported defect. AS3 has no "key released" branch: `isSit` survives `keySit` going
            // false, which is what makes crouch a state and lets the player crawl.
            Assert.IsFalse(CrouchStateMath.ShouldEndCrouch(upHeld: false, sitHeld: false, standing: true),
                "releasing Down must leave the player crouched — the crouch is a latch");
        }

        [Test]
        public void ShouldEndCrouch_UpStandsYouUp_ButOnlyWhenDownIsNotAlsoHeld()
        {
            Assert.IsTrue(CrouchStateMath.ShouldEndCrouch(upHeld: true, sitHeld: false, standing: true),
                "AS3 :2884 — `isSit && keyBeUp && !keySit`");

            Assert.IsFalse(CrouchStateMath.ShouldEndCrouch(upHeld: true, sitHeld: true, standing: true),
                "holding both keys must not stand the player up");
        }

        [Test]
        public void ShouldEndCrouch_LeavingTheGroundStandsYouUp()
        {
            Assert.IsTrue(CrouchStateMath.ShouldEndCrouch(upHeld: false, sitHeld: true, standing: false),
                "AS3 :2889 — `isSit && !stay`");
        }

        /// <summary>
        /// The latch, walked over a timeline, because the two bugs it replaces are both invisible in a
        /// single frame: a level and a latch agree on the frame the key goes down.
        /// </summary>
        [Test]
        public void Latch_SurvivesTheKeyRelease_AndEndsOnUp()
        {
            bool crouching = false;
            float held = 0f;
            const float dt = 1f / 50f;   // the project's fixed timestep, not 30 Hz
            float required = CrouchStateMath.RequiredHoldSeconds(false, CanonicalFps);

            // 1. Hold Down for 200 ms on solid ground.
            for (int i = 0; i < 10; i++)
            {
                held += dt;
                if (!crouching && held >= required)
                {
                    crouching = true;
                }
            }

            Assert.IsTrue(crouching, "holding Down must crouch");

            // 2. Release Down and keep walking: the crouch must persist. This is the whole fix.
            for (int i = 0; i < 60; i++)
            {
                bool sitHeld = false;
                bool upHeld = false;
                held = sitHeld ? held + dt : 0f;

                if (crouching && CrouchStateMath.ShouldEndCrouch(upHeld, sitHeld, standing: true))
                {
                    crouching = false;
                }
            }

            Assert.IsTrue(crouching, "one second of walking with Down released must leave the player crouched");

            // 3. Press Up: now it ends.
            Assert.IsTrue(CrouchStateMath.ShouldEndCrouch(upHeld: true, sitHeld: false, standing: true),
                "Up must end the crouch");
        }

        // ── The roll ─────────────────────────────────────────────────────────────────────────────

        [Test]
        public void ShouldRoll_FromASprint()
        {
            // Running right, facing right, run key held, Down just pressed.
            Assert.IsTrue(CrouchStateMath.ShouldRoll(
                speedBeforeCrouch: RunSpeed, walkSpeed: WalkSpeed,
                horizontalVelocity: RunSpeed, facingDirection: 1, horizontalInput: 1f,
                runHeld: true, runForced: false));
        }

        [Test]
        public void ShouldRoll_NotFromAWalk()
        {
            Assert.IsFalse(CrouchStateMath.ShouldRoll(
                speedBeforeCrouch: WalkSpeed, walkSpeed: WalkSpeed,
                horizontalVelocity: WalkSpeed, facingDirection: 1, horizontalInput: 1f,
                runHeld: true, runForced: false),
                "a walk is 1.0x walkSpeed, below the oracle's 1.6x gate");
        }

        [Test]
        public void ShouldRoll_NotFromTheSitSpeed_WhichIsWhatTheNextFrameSees()
        {
            // This is the property that makes the roll an ENTRY-only transition: one frame after the
            // crouch latches, maxSpeed is sitSpeed. Feeding the live speed in would make the roll
            // unreachable — which is how it was shipped (the clip was never referenced at all).
            Assert.IsFalse(CrouchStateMath.ShouldRoll(
                speedBeforeCrouch: SitSpeed, walkSpeed: WalkSpeed,
                horizontalVelocity: SitSpeed, facingDirection: 1, horizontalInput: 1f,
                runHeld: true, runForced: false));
        }

        [Test]
        public void ShouldRoll_NotBackpedalling()
        {
            // AS3 `dx * storona > 0`: moving against the facing does not roll.
            Assert.IsFalse(CrouchStateMath.ShouldRoll(
                speedBeforeCrouch: RunSpeed, walkSpeed: WalkSpeed,
                horizontalVelocity: -RunSpeed, facingDirection: 1, horizontalInput: -1f,
                runHeld: true, runForced: false));
        }

        [Test]
        public void ShouldRoll_NotWithTheRunKeyAlone()
        {
            // AS3 `keyRun && (keyLeft || keyRight)` — the run key with no direction held is not a run,
            // even when the body is still coasting at run speed. The velocity is deliberately non-zero:
            // with zero it would fail the facing test instead, and the gesture term would be untested.
            Assert.IsFalse(CrouchStateMath.ShouldRoll(
                speedBeforeCrouch: RunSpeed, walkSpeed: WalkSpeed,
                horizontalVelocity: RunSpeed, facingDirection: 1, horizontalInput: 0f,
                runHeld: true, runForced: false));
        }

        [Test]
        public void ShouldRoll_OnAForcedRun_WithoutTheKeyOrADirectionKey()
        {
            // AS3 `(runForever || keyRun && (left||right))`: the forced run substitutes for the whole
            // key expression. The player is still moving (momentum), so the facing test can pass.
            Assert.IsTrue(CrouchStateMath.ShouldRoll(
                speedBeforeCrouch: RunSpeed, walkSpeed: WalkSpeed,
                horizontalVelocity: RunSpeed, facingDirection: 1, horizontalInput: 0f,
                runHeld: false, runForced: true));
        }

        [Test]
        public void ShouldRoll_WithNoWalkSpeed_IsFalseRatherThanADivision()
        {
            Assert.IsFalse(CrouchStateMath.ShouldRoll(0f, 0f, 0f, 1, 1f, true, true));
        }

        [Test]
        public void RollHandoffFrame_IsFifteen()
        {
            // AS3 UnitPlayer.as:4417 — `animState == "roll" && body.currentFrame >= 15` → polz.
            Assert.That(CrouchStateMath.RollHandoffFrame, Is.EqualTo(15));
        }

        // ── Source lints ─────────────────────────────────────────────────────────────────────────
        //
        // The rules above can be perfect while the call site ignores them — this project's recurring
        // failure (hoof strike, loot floor). A MonoBehaviour cannot be exercised in the offline
        // harness, so the wiring is pinned by reading it.

        private const string LocomotionControllerPath =
            "Entities/Player/PlayerLocomotionController.cs";

        private const string AnimStatesPath = "Character/Animation/LocomotionAnimStates.cs";

        private const string AnimDriverPath = "Character/Animation/CharacterAnimationDriver.cs";

        private const string PlayerAnimAssetPath =
            "Data/Resources/Characters/PlayerAnimationDefinition.asset";

        [Test]
        public void Lint_CrouchIdle_RendersThePoseBanksCrouchFrame_NotAMissingClip()
        {
            string source = ReadKeepStrings(AnimStatesPath);
            string body = SourceLint.MethodBody(source, "class CrouchIdleAnimState");

            // Negative control: there is no `down` clip in the imported definition, so naming one made
            // ValidatedStateName return null and the driver fall back to the STANDING pose.
            Assert.That(body, Does.Not.Contain("\"down\""),
                "the crouch idle must not name the `down` clip — the definition has no such clip, and " +
                "the fallback renders the standing pose");

            // Positive: the pose bank, at AS3's `getStayFrame() == 2` as a 0-based index.
            Assert.That(body, Does.Contain("\"stay\""), "the crouch idle must play the pose bank");
            Assert.That(body, Does.Contain("CrouchPoseFrame = 1"),
                "AS3 getStayFrame() returns 2 for isSit (UnitPlayer.as:4726); the bank is 0-based here");
        }

        [Test]
        public void Lint_CrouchIdle_DoesNotCompeteWithTheCrawlOrTheRoll()
        {
            string source = ReadKeepStrings(AnimStatesPath);

            string crouch = SourceLint.MethodBody(source, "class CrouchIdleAnimState");
            Assert.That(crouch, Does.Contain("NormalizedMoveSpeed < 0.01f"),
                "crouch idle is the STILL pose; moving while crouched is polz/roll");

            string roll = SourceLint.MethodBody(source, "class RollAnimState");
            Assert.That(roll, Does.Contain("IsCrouchRoll"),
                "the roll must be gated on the movement layer's entry-frame decision");
            Assert.That(roll, Does.Contain("NormalizedMoveSpeed"),
                "the roll lives in AS3's sitting-AND-MOVING branch; standing still must drop it");
            Assert.That(roll, Does.Contain("RollHandoffFrame"),
                "the roll must hand over to polz at frame 15, as UnitPlayer.as:4417 does");
            Assert.That(roll, Does.Contain("=> -1"),
                "the roll must ADVANCE its own frames; overriding them freezes the clip and the " +
                "handover never happens");
        }

        [Test]
        public void Lint_DriverRegistersTheRollState()
        {
            string source = ReadKeepStrings(AnimDriverPath);
            string build = SourceLint.MethodBody(source, "void BuildStates()");

            Assert.That(build, Does.Contain("new RollAnimState()"),
                "an unregistered state is an unreachable clip — exactly what `roll` was");
            Assert.That(build, Does.Contain("roll,"),
                "the state must also be added to the evaluated list, not merely constructed");
        }

        [Test]
        public void Lint_PlayerDefinition_HasTheRollClipAndACrouchPoseToHold()
        {
            string asset = ReadKeepStrings(PlayerAnimAssetPath);

            Assert.That(asset, Does.Contain("- stateName: roll"),
                "the roll clip must be imported, or the state falls back to `stay`");
            Assert.That(asset, Does.Contain("- stateName: stay"),
                "the crouch idle holds a frame of the `stay` pose bank");
        }

        [Test]
        public void Lint_LocomotionAppliesTheLatch_NotTheRawKeyLevel()
        {
            string source = ReadStripped(LocomotionControllerPath);
            string update = SourceLint.MethodBody(source, "private void UpdateCrouch(");

            // The rules must actually be called. A perfect CrouchStateMath with a call site that
            // ignores it is the failure mode this lint exists for.
            Assert.That(update, Does.Contain("CrouchStateMath.ShouldEndCrouch"),
                "the release test must come from the latch rules");
            Assert.That(update, Does.Contain("CrouchStateMath.ShouldRoll"),
                "the roll must be decided at the latch, where the entry speed still exists");
            Assert.That(update, Does.Contain("CrouchStateMath.RequiredHoldSeconds"),
                "the hold debounce must gate the latch");

            // Positive: the fixed step calls the latch at all.
            string fixedStep = SourceLint.MethodBody(source, "private void FixedUpdate()");
            Assert.That(fixedStep, Does.Contain("UpdateCrouch(state, dt)"),
                "the latch must be advanced once per fixed step");

            // The dash is the one other action the oracle gates on `!isSit` (:2592/:2615). With a
            // latched crouch that gate is reachable input, so it must not be dropped.
            string dash = SourceLint.MethodBody(source, "private void TryConsumeDash(");
            Assert.That(dash, Does.Contain("Crouching(state)"),
                "a crouched player must not dash — AS3 `!isSit` in the dash condition");

            // Negative control, on the whole file: the shipped defect was this exact expression, and
            // re-adding it anywhere in the controller — crouch, swim, drop-through — restores the
            // level semantics the latch exists to replace.
            Assert.That(source, Does.Not.Contain("_moveInput.y < -0.5f"),
                "crouch must not be recomputed from the Down key every step — that is the level form " +
                "that made the crouch end the moment the key came up");
        }

        [Test]
        public void Lint_LocomotionPublishesTheRollToTheAnimationLayer()
        {
            string source = ReadStripped(LocomotionControllerPath);

            Assert.That(source, Does.Contain("_crouchRoll"),
                "the entry-frame decision must reach PlayerMovementSnapshot.IsCrouchRoll");
            Assert.That(source, Does.Contain("Crouching(latestState)"),
                "the snapshot must publish the effective crouch (latch OR a blocked stand-up)");
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────────

        private static string ReadKeepStrings(string relativePath)
        {
            string fullPath = Path.Combine(
                SourceLint.ProjectRoot(), "Assets", "_PFE", relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.IsTrue(File.Exists(fullPath), "Lint target missing: " + fullPath);
            return SourceLint.StripCommentsKeepStrings(File.ReadAllText(fullPath));
        }

        private static string ReadStripped(string relativePath)
        {
            return SourceLint.ReadStripped(relativePath);
        }
    }
}
