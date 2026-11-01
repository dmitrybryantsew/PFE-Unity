using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the <b>wiring</b> of the enemy aim point — that every armed brain hands its mount the
    /// <c>cel</c> point and not the target's transform origin.
    ///
    /// <para><b>Why a source lint.</b> The defect being guarded is the one that shipped: the arithmetic
    /// was right in two places (the boss raider and the ultra boss) and absent in five others, and the
    /// five were invisible — an alicorn bolt fired into the floor looks like a projectile that missed,
    /// not like a coordinate bug, and nothing throws. Every one of those five is a <c>MonoBehaviour</c>
    /// method that needs a live room, a spawned unit and a weapon mount to run, so a behavioural fixture
    /// cannot execute outside the editor; the regressions are textual anyway — a call handed the wrong
    /// expression.</para>
    ///
    /// <para><b>Every lint is paired.</b> A presence check alone passes on a body extracted from the
    /// wrong method; an absence check alone passes on an empty string. Each case below asserts a token
    /// it wants <i>and</i> a token that proves the extracted region is the right one, and
    /// <see cref="TheExtractor_ScopesToTheNamedMethod"/> is the control for the extractor itself.</para>
    /// </summary>
    [TestFixture]
    public class EnemyAimWiringLintTests
    {
        private const string SensorsPath = "Entities/Enemies/Core/EnemySensors.cs";
        private const string MountPath = "Systems/Weapons/EnemyWeaponMount.cs";

        // ── The control: the extractor must scope, or every lint below is vacuous ──

        [Test]
        public void TheExtractor_ScopesToTheNamedMethod()
        {
            string source = SourceLint.ReadStripped("Entities/Enemies/Archetypes/Robot/RobotBrain.cs");

            string body = SourceLint.MethodBody(
                source, "protected virtual void SetWeaponTrigger(bool firing)");

            Assert.That(body, Does.Contain("RobotCtrl"),
                "positive control: this has to be RobotBrain.SetWeaponTrigger");

            Assert.That(body, Does.Not.Contain("SetSecondaryTrigger"),
                "if MethodBody returned the whole file, every scoped assertion in this fixture would " +
                "pass or fail for the wrong reason");
        }

        // ── The global rule: no enemy aims at a transform origin, anywhere ─────

        /// <summary>
        /// <b>The guard that catches a new brain, not just the five that were wrong.</b> Walks every
        /// production file under <c>Entities/</c> and fails on a line that both publishes an aim
        /// (<c>SetAimTarget</c> on a mount, <c>SetWeaponAimPoint</c> on a controller) and mentions
        /// <c>transform.position</c>. That pair is the bug in one token: both take a world-space point,
        /// and the only world point a <c>UnitController</c> exposes directly is its feet.
        ///
        /// <para>The walk asserts how many files it saw and that it found aim sites at all — an empty
        /// directory or a moved tree would otherwise turn this into a green no-op, which is the failure
        /// mode this project keeps paying for.</para>
        /// </summary>
        [Test]
        public void NoProductionAimSite_PointsAtATransformOrigin()
        {
            string pfeRoot = Path.Combine(SourceLint.ProjectRoot(), "Assets", "_PFE");
            string entities = Path.Combine(pfeRoot, "Entities");
            Assert.IsTrue(Directory.Exists(entities), "Lint target missing: " + entities);

            string[] files = Directory.GetFiles(entities, "*.cs", SearchOption.AllDirectories);
            Assert.Greater(files.Length, 50,
                "the walk must see the real tree — a handful of files means the path moved and every " +
                "assertion below would pass against nothing");

            string[] publishers = { "SetAimTarget(", "SetWeaponAimPoint(" };

            var offenders = new List<string>();
            int aimSites = 0;

            foreach (string file in files)
            {
                string stripped = SourceLint.StripCommentsAndStrings(File.ReadAllText(file));

                foreach (string line in stripped.Split('\n'))
                {
                    bool publishes = false;
                    foreach (string token in publishers)
                    {
                        if (line.IndexOf(token, System.StringComparison.Ordinal) >= 0) { publishes = true; break; }
                    }

                    if (!publishes) continue;

                    aimSites++;
                    if (line.IndexOf("transform.position", System.StringComparison.Ordinal) >= 0)
                    {
                        offenders.Add(Path.GetFileName(file) + ": " + line.Trim());
                    }
                }
            }

            Assert.Greater(aimSites, 0,
                "positive control: the walk must find the aim calls it is checking. Zero means the " +
                "needle or the tree changed and this lint proves nothing.");

            Assert.IsEmpty(offenders,
                "an enemy weapon aimed at transform.position fires at the target's FEET " +
                "(Unit.as:1875: Y2 = Y), i.e. into the floor tile she stands on. Use " +
                "EnemySensors.GetUnitAimWorld(target) — Unit.as:4707-4708. Offenders:\n  " +
                string.Join("\n  ", offenders));
        }

        // ── The seam itself ───────────────────────────────────────────────────

        /// <summary>
        /// <see cref="PFE.Entities.Enemies.EnemySensors"/> must keep <b>two</b> points: the centre (for
        /// hearing and range, which AS3 compares centre-to-centre) and the aim point (which carries the
        /// quarter-width lead). Collapsing them would fix the aim and quietly move every perception
        /// threshold by a quarter body.
        /// </summary>
        [Test]
        public void GetUnitAimPixels_UsesTheAimRule_NotTheCentre()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(SensorsPath),
                "public static Vector2 GetUnitAimPixels(UnitController unit)");

            Assert.That(body, Does.Contain("SpriteSizePixels"),
                "positive control: this has to be GetUnitAimPixels");

            Assert.That(body, Does.Contain("EnemyAimMath.TargetCel"),
                "the aim point must be the rule's — TargetCel is the one implementation of " +
                "Unit.as:4707-4708");

            Assert.That(body, Does.Not.Contain("GetUnitCenterPixels"),
                "the centre helper has no lead; routing the aim through it is the half-fix that " +
                "shipped for the raiders");
        }

        [Test]
        public void GetUnitCenterPixels_StaysACentre()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(SensorsPath),
                "public static Vector2 GetUnitCenterPixels(UnitController unit)");

            Assert.That(body, Does.Contain("Stats.Height"),
                "positive control: this has to be GetUnitCenterPixels");

            Assert.That(body, Does.Not.Contain("0.25f"),
                "hearing compares centre to centre (Unit.as:126332-126353). Adding the quarter-width " +
                "lead here would move every perception threshold by a quarter body — a silent " +
                "stealth/detection change nobody asked for.");
        }

        // ── Each armed brain, by the method that publishes the aim ────────────

        [TestCase("Entities/Enemies/Archetypes/Alicorn/AlicornBrain.cs",
                  "protected virtual void UpdateWeaponFire()",
                  "EnemyWeaponMount",
                  "alilight / alipsy — the reported miss")]
        [TestCase("Entities/Enemies/Archetypes/ArmedShooter/ArmedShooterBrain.cs",
                  "protected virtual void SetWeaponTrigger(bool firing)",
                  "WeaponMount",
                  "raiders, mercs, encl, ranger")]
        [TestCase("Entities/Enemies/Archetypes/ArmedShooter/ArmedShooterBrain.cs",
                  "protected Vector2 ResolveAimWorldPoint()",
                  "TileQueryConstants",
                  "the per-tick barrel tracking, which must agree with the trigger")]
        [TestCase("Entities/Enemies/Archetypes/Robot/RobotBrain.cs",
                  "protected virtual void SetWeaponTrigger(bool firing)",
                  "RobotCtrl",
                  "gutsy / dron flamethrower")]
        [TestCase("Entities/Enemies/Archetypes/Robot/RobotBrain.cs",
                  "protected virtual void SetSecondaryTrigger(bool firing)",
                  "SecondaryMount",
                  "dopWeapon — the same cel as the primary")]
        [TestCase("Entities/Enemies/Archetypes/Trap/TrapBrain.cs",
                  "protected virtual void TickTurret(int tickIndex)",
                  "TurretAIStateMath",
                  "turrets (UnitTurret.as:531 setCel(loc.gg))")]
        [TestCase("Entities/Enemies/Archetypes/ZombieBrain.cs",
                  "private Vector2 ResolveAimWorldPoint()",
                  "GetEyePositionPixels",
                  "the zombie spit super")]
        [TestCase("Entities/Enemies/Bosses/BossAlicornBrain.cs",
                  "void Aim()",
                  "SetWeaponAimPoint",
                  "the boss the report names")]
        [TestCase("Entities/Enemies/Bosses/BossNecrBrain.cs",
                  "protected override void ExecuteAttackAction()",
                  "_isShadow",
                  "the necromancer boss's shadow form")]
        [TestCase("Entities/Enemies/Bosses/BossRaiderBrain.cs",
                  "protected virtual void SetWeaponTrigger(bool firing)",
                  "WeaponMount",
                  "the raider boss's flamer")]
        [TestCase("Entities/Enemies/Bosses/BossRaiderBrain.cs",
                  "protected virtual void RefreshCel()",
                  "_celX",
                  "the movement goal and the barrel must be the same point")]
        public void ArmedBrain_AimsAtTheCelPoint(string path, string signature, string positiveControl,
                                                 string why)
        {
            string body = SourceLint.MethodBody(SourceLint.ReadStripped(path), signature);

            Assert.That(body, Does.Contain(positiveControl),
                "positive control: the extractor must have found the right body for " + why);

            Assert.That(body, Does.Contain("GetUnitAim"),
                why + ": the mount must be handed `setCel`'s point (Unit.as:4707-4708), not the " +
                "target's transform origin. `GetUnitAimWorld` for a mount, `GetUnitAimPixels` for a " +
                "caller that stores `_celX`/`_celY` in pixels — both are the same seam.");

            AssertEveryAimCallComesFromTheSeam(body, why);
        }

        /// <summary>
        /// <b>Every</b> <c>SetAimTarget</c> in the body must be fed by the seam — not merely "one of
        /// them is".
        ///
        /// <para>This assertion exists because the mutation control found the weaker form vacuous:
        /// reverting only the alicorn's <i>primary</i> mount to <c>transform.position</c> left the
        /// psychic weapon's call intact, so a body-level "contains GetUnitAim" check still passed while
        /// the reported bug was back in the file. A method with two aim sites needs two checks.</para>
        ///
        /// <para>The allow-list is the three legitimate argument forms: the two seam helpers, the
        /// per-brain resolver that wraps them, and <c>_celX</c>/<c>_celY</c> (the boss raider keeps its
        /// <c>cel</c> in pixels and converts at the mount).</para>
        /// </summary>
        private static void AssertEveryAimCallComesFromTheSeam(string body, string why)
        {
            string[] accepted = { "GetUnitAim", "ResolveAimWorldPoint", "_celX" };
            var offenders = new List<string>();

            foreach (string line in body.Split('\n'))
            {
                if (line.IndexOf("SetAimTarget(", System.StringComparison.Ordinal) < 0) continue;

                bool ok = false;
                foreach (string token in accepted)
                {
                    if (line.IndexOf(token, System.StringComparison.Ordinal) >= 0) { ok = true; break; }
                }

                if (!ok) offenders.Add(line.Trim());
            }

            Assert.IsEmpty(offenders,
                why + ": every SetAimTarget must be fed by EnemySensors.GetUnitAimWorld/Pixels (or the " +
                "resolver that wraps them). These calls are not:\n  " + string.Join("\n  ", offenders));
        }

        /// <summary>
        /// <b>The negative control for the table above.</b> A pet is the one archetype whose
        /// <c>setCel</c> override drops the lead (<c>UnitPet.as:815-816</c>), so a blanket
        /// "everyone uses GetUnitAimWorld" cleanup — which would make all eleven cases above pass —
        /// is wrong here. This case is what makes the table a rule rather than a slogan.
        /// </summary>
        [Test]
        public void PetBrain_AimsAtTheCentre_WithoutTheLead()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped("Entities/Pets/PetBrain.cs"),
                "void UpdateWeaponFire(RoomInstance room, Vector2 self)");

            Assert.That(body, Does.Contain("PrimaryWeaponMount"),
                "positive control: this has to be PetBrain.UpdateWeaponFire");

            Assert.That(body, Does.Contain("GetUnitCenterPixels"),
                "UnitPet.setCel writes celX = celUnit.X — no scX/4 term");

            Assert.That(body, Does.Not.Contain("GetUnitAimWorld"),
                "a pet must not inherit the quarter-width lead its oracle override removed");
        }

        // ── The last resort: the mount's own idle point ───────────────────────

        /// <summary>
        /// A brain that publishes nothing falls back to the mount's facing-only point, which used to
        /// aim from the owner's <b>feet</b> — the same class of bug, one layer down, and the reason
        /// every idle armed unit's gun tilted at the floor. The oracle's own no-target
        /// <c>cel</c> is two body widths forward at head height (<c>UnitRaider.as:682-683</c>).
        /// </summary>
        [Test]
        public void TheMountsIdleFallback_UsesTheOracleAheadPoint()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(MountPath),
                "private Vector2 ResolveIdleAimTarget()");

            Assert.That(body, Does.Contain("_owner"),
                "positive control: this has to be ResolveIdleAimTarget");

            Assert.That(body, Does.Contain("EnemyAimMath.AheadCel"),
                "the idle point is celY = Y - scY, celX = X + scX * storona * 2");

            Assert.That(body, Does.Not.Contain("FeetWorldY"),
                "anchoring the idle aim on the feet is the bug — it points the barrel at the floor");
        }
    }
}
