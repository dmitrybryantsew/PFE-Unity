using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// Pins the <b>wiring</b> of the <c>demask</c> writers — the half of the stealth work that is a
    /// <i>call</i> rather than an arithmetic fact, and so cannot be exercised by
    /// <see cref="StealthMathTests"/>.
    ///
    /// <para><b>Why a lint rather than a behavioural test.</b> Every writer here reaches perception
    /// through a <c>MonoBehaviour</c> that this project's offline harness cannot construct — a
    /// <c>ProjectileSpawner</c> needs a component graph, and <c>TickStealth</c> needs a live unit. A lint
    /// is the only form of these guards that executes in the harness, so it is the one that can actually
    /// go red. <see cref="SourceLint"/> owns the reading and stripping, and the reason stripping is
    /// required at all.</para>
    ///
    /// <para><b>The two failure shapes this exists to catch.</b> (1) The reveal is written by the
    /// arithmetic and never called — the "field computed and thrown away" shape this project has hit
    /// repeatedly, where a value exists, is correct, and reaches nothing. (2) A per-tick source is
    /// <i>declared</i> on <c>UnitController</c> and never read by <c>TickStealth</c>, or read and never
    /// supplied by <c>PlayerController</c> — in either case the hook looks wired and is dead, exactly
    /// like the trap sweep that dealt no damage while every fixture stayed green.</para>
    /// </summary>
    [TestFixture]
    public sealed class StealthWiringLintTests
    {
        private const string SpawnerPath = "Systems/Weapons/ProjectileSpawner.cs";
        private const string UnitControllerPath = "Entities/Units/UnitController.cs";
        private const string PlayerControllerPath = "Entities/Player/PlayerController.cs";

        /// <summary>
        /// The fire reveal — <c>weapon/Weapon.as:1575-1578</c> — must be called from the one place every
        /// plan batch passes through, must read the weapon's <b>own</b> <c>shine</c>, and must keep the
        /// noise write beside it.
        /// </summary>
        [Test]
        public void FirePath_StampsTheWeaponsOwnShine_AndKeepsItsNoiseTwin()
        {
            string spawner = SourceLint.ReadStripped(SpawnerPath);

            string spawnFromPlans =
                SourceLint.MethodBody(spawner, "public void SpawnFromPlans(IReadOnlyList<ShotPlan> plans)");
            string revealShot = SourceLint.MethodBody(spawner, "private void RevealShot(ShotPlan plan)");

            // The reveal must be invoked from the batch the spawner already iterates — both the player
            // and every enemy mount reach the spawner through this one method, so a call anywhere else
            // covers only half the game.
            Assert.That(spawnFromPlans, Does.Contain("RevealShot("),
                "ProjectileSpawner.SpawnFromPlans no longer calls RevealShot, so firing reveals nobody. " +
                "This is the sole reader of ShotCues.ShineRadius — without it the weapon's <vis shine> " +
                "is data that reaches nothing.");

            // ...and its noise twin must still be there. In the oracle the two are adjacent lines
            // (Weapon.as:1575-1583); a change that traded one for the other would silently swap a shot's
            // reveal for its noise, or vice versa, and both would still look "wired".
            Assert.That(spawnFromPlans, Does.Contain("AnnounceShot("),
                "SpawnFromPlans no longer calls AnnounceShot — a shot's noise (ShotCues.NoiseRadius) " +
                "must not be lost in exchange for its reveal; the oracle writes both.");

            // The value must come from the plan, not a literal. Cues.ShineRadius is the weapon's own
            // <vis shine>; a hard-coded 500 would make a silenced pistol exactly as loud as a minigun.
            Assert.That(revealShot, Does.Contain("Cues.ShineRadius"),
                "RevealShot no longer reads Cues.ShineRadius. The oracle reads the weapon's own shine " +
                "(Weapon.as:1575), so a literal — or a read of the default — would flatten every weapon " +
                "to the same reveal.");

            Assert.That(revealShot, Does.Contain("RaiseDemask("),
                "RevealShot no longer calls RaiseDemask, so the read shine reaches no state.");

            // The cue is already in AS3 pixels — unlike NoiseRadius, which the controllers divide by
            // PpuScale. A "helpful" conversion here is the plausible wrong fix and would shrink the
            // reveal 100x, so the absence is asserted rather than left to a comment.
            Assert.That(revealShot, Does.Not.Contain("PpuScale"),
                "RevealShot divides the shine by PpuScale. demask lives in the oracle's pixel domain " +
                "(DemaskMax = 200, shine = 500); the controllers pass _def.shineRadius raw, where they " +
                "pass _def.noiseRadius / PpuScale. Converting here shrinks the reveal by 100x.");
        }

        /// <summary>
        /// The two per-tick <c>demask</c> sources — levitation and telekinesis — must be read by
        /// <c>TickStealth</c> and supplied by <c>PlayerController</c>.
        /// </summary>
        [Test]
        public void StateRevealSources_AreReadByTickStealth_AndSuppliedByThePlayer()
        {
            string unit = SourceLint.ReadStripped(UnitControllerPath);
            string player = SourceLint.ReadStripped(PlayerControllerPath);

            string tickStealth =
                SourceLint.MethodBody(unit, "public void TickStealth(Vector2 velocityPixelsPerFrame)");

            // Both sources must actually be passed into the arithmetic. Declaring a virtual source and
            // forgetting to read it is the "hook that exists and is dead" shape: the state would look
            // wired and never reveal anyone.
            Assert.That(tickStealth, Does.Contain("StealthLevitatingSource"),
                "TickStealth no longer passes StealthLevitatingSource into StealthMath.Tick, so " +
                "levitation reveals nobody (unit/UnitPlayer.as:1113-1116).");

            Assert.That(tickStealth, Does.Contain("StealthTelekinesisSource"),
                "TickStealth no longer passes StealthTelekinesisSource into StealthMath.Tick, so " +
                "holding an object telekinetically reveals nobody (unit/UnitPlayer.as:1239-1242).");

            // ...and the player must supply both. The base returns false, so a missing override is
            // completely silent — the states simply never reveal, which is indistinguishable from "the
            // feature was not built" until someone reads the file.
            Assert.That(player, Does.Contain("override bool StealthLevitatingSource"),
                "PlayerController no longer overrides StealthLevitatingSource. The base returns false, " +
                "so levitation would silently stop revealing the player.");

            Assert.That(player, Does.Contain("override bool StealthTelekinesisSource"),
                "PlayerController no longer overrides StealthTelekinesisSource. The base returns false, " +
                "so telekinesis would silently stop revealing the player.");

            // ...and from the right states: the override must read the controllers that own them, not
            // re-derive a flag of its own.
            Assert.That(player, Does.Contain("IsLevitating"),
                "The levitation override must read PlayerLocomotionController.IsLevitating — the " +
                "component that owns the state — rather than a second source of truth.");

            Assert.That(player, Does.Contain("IsHoldingObject"),
                "The telekinesis override must read PlayerTelekinesisController.IsHoldingObject — the " +
                "component that owns the state — rather than a second source of truth.");
        }
    }
}
