using System;
using System.Collections.Generic;
using System.Reflection;
using MessagePipe;
using NUnit.Framework;
using UnityEngine;
using PFE.Core;
using PFE.Core.Messages;
using PFE.Core.Rng;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Map;
// Required, and not obviously so: RoomInstance lives in PFE.Systems.Map, but the ITileQueryService
// signatures this stub must match (CaptureFullState/ApplyFullState) name TileStateSnapshot, which
// lives here. Removing this line breaks the stub's interface contract.
using PFE.Systems.Map.Serialization;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Particles;
using PFE.Systems.Particles.Adapters;
using PFE.Systems.Weapons;
using PFE.Tests.Editor.Core;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the <b>emit site</b> of the blood spray — the half of slice 4a that
    /// <see cref="BloodSprayRules"/> cannot reach.
    ///
    /// <para><b>Why this is a separate fixture from <c>BloodSprayRulesTests</c>.</b> That one proves the
    /// <i>decision</i>: which blood, which damage types, the two anchors, the gib arithmetic. This one
    /// proves the <i>wiring</i>, and every failure mode here is a silent one — a spray drawn from the
    /// wrong anchor looks like blood, a spray that took its draws from the combat stream changes no
    /// number anyone can see, and a spray that never happens looks like a unit that simply has no blood
    /// authored. None of the three throws, and none of them is visible in
    /// <c>BloodSprayRulesTests</c>, because the rules are handed their context by a test rather than by
    /// <c>DamageSystem</c>.</para>
    ///
    /// <para><b>Three things are asserted that only the wiring can get wrong.</b></para>
    /// <list type="number">
    ///   <item><description><b>Both anchors, in one hit.</b> A bullet anchors the spray at its own
    ///     contact point and the gib at the target's origin, in the same call — AS3 reads
    ///     <c>param3.X/Y</c> at <c>:3865</c> and <c>X</c>/<c>Y</c> at <c>:3897</c>. Wiring that reused
    ///     one anchor for both would still emit two particles at plausible places.</description></item>
    ///   <item><description><b>The direction's Y is mirrored, the X is not.</b> AS3's Y runs down and the
    ///     port's runs up, so a velocity override forwarded verbatim throws the spray at the ceiling
    ///     instead of the floor. It is a sign error on a float, so it is invisible.</description></item>
    ///   <item><description><b>The draws come from the presentation stream.</b> The oracle has one global
    ///     <c>Math.random()</c>, so its blood draws do shift the knockback roll <c>otbros</c> takes
    ///     immediately afterwards. The port deliberately splits the stream; the assertion below is the
    ///     falsifiable form of "deliberately" — a scripted presentation stream records exactly how many
    ///     draws the blood path took, and it is zero if the code ever goes back to the combat
    ///     stream.</description></item>
    /// </list>
    ///
    /// <para><b>Everything here runs offline.</b> The adapter, <c>RoomInstance</c>,
    /// <c>WorldCoordinates</c> and <c>DamageSystem</c> are all reachable from a plain host, which
    /// <c>RoomParticleEmitterTests</c> and <c>DamageSystemTests</c> already demonstrate — so the emit
    /// site is genuinely verifiable without opening the editor.</para>
    ///
    /// <para><b>A known readback gap, recorded rather than asserted.</b> With no room pushed the blood
    /// path refuses through <see cref="RoomParticleEmitter.TryToAs3Local"/> directly, which — unlike
    /// <see cref="RoomParticleEmitter.Emit"/> — does not bump
    /// <see cref="RoomParticleEmitter.RefusedWithoutRoom"/>. So the counter that exists to answer "why do
    /// particles never appear" is blind to this one path. It is not fixed here because the room push is
    /// all-or-nothing for the whole particle system (the projectile visuals and the water query refuse
    /// on the same condition), so the counter is not the thing that would have to be read to notice.
    /// The test below pins the behaviour — no emits — and not the counter.</para>
    /// </summary>
    [TestFixture]
    public class DamageSystemBloodSprayTests
    {
        // A 25-tile room is 1000 px tall, which makes every expected Y a round number below.
        private const int RoomHeight = 25;

        /// <summary>The target's Unity world position in every test: AS3 room-local (500, 800).</summary>
        private static readonly Vector3 UnitOrigin = new Vector3(5f, 2f, 0f);

        /// <summary>A 40×60 sprite — AS3 <c>scX</c>/<c>scY</c>, so the standing spray drops 30 px.</summary>
        private static readonly Vector2 UnitSpriteSize = new Vector2(40f, 60f);

        // ── Fakes ────────────────────────────────────────────────────────────────────────────

        /// <summary>Records what it was asked to emit, so the coordinates can be asserted directly.</summary>
        private sealed class RecordingWorld : IParticleWorld
        {
            public readonly List<(string Id, float X, float Y, ParticleSpec Spec)> Emits =
                new List<(string, float, float, ParticleSpec)>();

            public bool Emit(string id, float x, float y) => Emit(id, x, y, null);

            public bool Emit(string id, float x, float y, ParticleSpec overrides)
            {
                Emits.Add((id, x, y, overrides));
                return true;
            }

            public bool Has(string id) => true;
            public IReadOnlyCollection<string> UnknownIds => Array.Empty<string>();
            public void BeginTick() { }
        }

        /// <summary>
        /// A tile query that answers only what the adapter asks. Every other member throws rather than
        /// returning a default, so a future edit that starts calling one names itself instead of
        /// passing on a meaningless value. Same shape as the stub in <c>RoomParticleEmitterTests</c>.
        /// </summary>
        private sealed class StubTileQuery : ITileQueryService
        {
            public RoomInstance RoomInstance;
            public Vector2 Origin;

            TileQueryBackend ITileQueryService.Backend => TileQueryBackend.Unified;
            RoomInstance ITileQueryService.Room => RoomInstance;
            Vector2 ITileQueryService.OriginPixel => Origin;

            bool ITileQueryService.IsSolidAt(Vector2Int tileCoord) => throw new NotSupportedException();
            bool ITileQueryService.CheckCollision(Rect boundsPx, TileQueryOptions options) => throw new NotSupportedException();
            float ITileQueryService.GetGroundHeight(Vector2 positionPx) => throw new NotSupportedException();
            bool ITileQueryService.IsOnGround(Rect boundsPx) => throw new NotSupportedException();
            bool ITileQueryService.IsOnGround(Rect boundsPx, TileQueryOptions options) => throw new NotSupportedException();
            bool ITileQueryService.TryGetSupportSpan(Rect boundsPx, TileQueryOptions options, out float supportLeftWorldPx, out float supportRightWorldPx) => throw new NotSupportedException();
            TileRaycastHit? ITileQueryService.Raycast(Vector2 originPx, Vector2 direction, float maxDistancePx) => throw new NotSupportedException();
            TileQueryFlags ITileQueryService.Classify(Vector2Int tileCoord) => throw new NotSupportedException();
            SurfaceKind ITileQueryService.ClassifySurface(Vector2Int tileCoord) => throw new NotSupportedException();
            TileMoveResult ITileQueryService.ResolveMove(in TileBox box, Vector2 delta, TileQueryFlags mask) => throw new NotSupportedException();
            bool ITileQueryService.ApplyDamage(Vector2 positionPx, int damage, int radiusTiles) => throw new NotSupportedException();
            void ITileQueryService.NotifyTilesMutated(RectInt tileRegion) => throw new NotSupportedException();
            TileStateSnapshot[] ITileQueryService.CaptureFullState() => throw new NotSupportedException();
            void ITileQueryService.ApplyFullState(TileStateSnapshot[] snapshot) => throw new NotSupportedException();
            TileMutation[] ITileQueryService.DrainMutations() => throw new NotSupportedException();
            void ITileQueryService.ApplyMutations(TileMutation[] mutations) => throw new NotSupportedException();
        }

        /// <summary>
        /// Returns scripted values, and <b>counts the draws consumed</b>. The count is the assertion in
        /// the stream test: a gate that returns before drawing must leave it at zero, and a hit that
        /// took its draws from somewhere else leaves it at zero too.
        /// </summary>
        /// <remarks>
        /// Unlike the copy in <c>DamageSystemTests</c>, <c>Range(int, int)</c> here <b>consumes a
        /// draw</b> and maps it onto the range, because the gib's variant is <c>Math.floor(rand * 3 +
        /// 1)</c> and a stub that always answered <c>minInclusive</c> would make the variant assertion
        /// vacuous.
        /// </remarks>
        private sealed class ScriptedRng : IRngService
        {
            private readonly Queue<float> _values;

            public int RollsConsumed { get; private set; }

            public ScriptedRng(params float[] values) => _values = new Queue<float>(values);

            public float NextFloat()
            {
                RollsConsumed++;
                return _values.Count > 0 ? _values.Dequeue() : 0f;
            }

            public IRngService GetStream(RngStream stream, int? salt = null) => this;

            public uint NextUInt() => 0u;
            public int NextInt(int maxExclusive) => Range(0, maxExclusive);

            public int Range(int minInclusive, int maxExclusive)
                => minInclusive + (int)(NextFloat() * (maxExclusive - minInclusive));

            public float Range(float min, float max) => min + NextFloat() * (max - min);
            public bool Chance(float probability) => NextFloat() < probability;
            public void Shuffle<T>(IList<T> list) { }
        }

        /// <summary>
        /// Splits the two streams the blood path touches: the <b>combat</b> stream is a real seeded PCG
        /// (so the damage formula behaves exactly as it does in production), and the
        /// <b>presentation</b> stream is whatever the test supplies. That separation is what makes the
        /// "presentation, not combat" assertion possible — a single RNG for both would give the same
        /// numbers either way.
        /// </summary>
        private sealed class SplitRng : IRngService
        {
            private readonly IRngService _combat = new PcgRngService(0xC0FFEEUL);
            private readonly IRngService _presentation;

            public readonly List<(RngStream Stream, int? Salt)> Requests =
                new List<(RngStream, int?)>();

            public SplitRng(IRngService presentation) => _presentation = presentation;

            public IRngService GetStream(RngStream stream, int? salt = null)
            {
                Requests.Add((stream, salt));
                return stream == RngStream.Presentation ? _presentation : _combat.GetStream(stream, salt);
            }

            public uint NextUInt() => _combat.NextUInt();
            public float NextFloat() => _combat.NextFloat();
            public int NextInt(int maxExclusive) => _combat.NextInt(maxExclusive);
            public int Range(int minInclusive, int maxExclusive) => _combat.Range(minInclusive, maxExclusive);
            public float Range(float min, float max) => _combat.Range(min, max);
            public bool Chance(float probability) => _combat.Chance(probability);
            public void Shuffle<T>(IList<T> list) => _combat.Shuffle(list);
        }

        /// <summary>
        /// A damageable that is <b>not</b> a unit — a crate, a mine, a turret. Deliberately does not
        /// implement <see cref="IBloodSpraySource"/>, which is the point of the negative test below.
        /// Every member is AS3's own field default, so the damage formula reduces to
        /// <c>baseDamage</c> and the expected numbers are exact rather than approximate.
        /// </summary>
        private class PlainTarget : IDamageable
        {
            public float Health = 1000f;

            public float MaxHealth { get; set; } = 1000f;
            public ArmourState Armour { get; set; } = ArmourState.None;
            public VulnerabilityData Vulnerabilities { get; set; } = VulnerabilityData.Neutral;
            public float SkinResistance { get; set; } = 0f;
            public EvasionState Evasion { get; set; } = EvasionState.Default;
            public float Knocked { get; set; } = 1f;
            public float Mass { get; set; } = 1f;
            public bool IsInvulnerable { get; set; } = false;
            public bool IsNonLiving { get; set; } = false;

            /// <summary>AS3 <c>this.stay</c> — grounded by default (see the D_SPARK rule).</summary>
            public bool IsGrounded { get; set; } = true;

            /// <summary>AS3 <c>this.inWater</c>.</summary>
            public bool IsInWater { get; set; } = false;

            /// <summary>AS3 <c>this.allVulnerMult</c> — identity by default.</summary>
            public float AllVulnerabilityMultiplier { get; set; } = 1f;

            /// <summary>AS3 <c>this.shithp</c> — <c>0</c> is "no shield".</summary>
            public float ShieldHp { get; set; } = 0f;

            /// <summary>AS3 <c>this.shitArmor</c> — inert while <see cref="ShieldHp"/> is 0.</summary>
            public float ShieldArmor { get; set; } = 20f;

            public DamageOutcome LastOutcome;

            public void TakeDamage(float damage) => Health -= damage;

            public bool ApplyDamage(in DamageOutcome outcome)
            {
                LastOutcome = outcome;
                Health -= outcome.HpDamage;
                return outcome.ArmourBroke;
            }

            public void ApplyKnockback(Vector2 impulse) { }

            public float CurrentHealth => Health;
            public bool IsAlive => Health > 0f;
        }

        /// <summary>
        /// A unit: a <see cref="PlainTarget"/> plus the three reads AS3's blood block needs. The
        /// interface members are implemented explicitly so the properties can be mutated as plain
        /// fields by a test without the property names colliding with the type names.
        /// </summary>
        private sealed class BleedingTarget : PlainTarget, IBloodSpraySource
        {
            public BloodType Blood = BloodType.Red;
            public Vector3 Origin = UnitOrigin;
            public Vector2 SpriteSize = UnitSpriteSize;

            BloodType IBloodSpraySource.BloodType => Blood;
            Vector2 IBloodSpraySource.SpriteSizePixels => SpriteSize;
            Vector3 IBloodSpraySource.WorldPosition => Origin;
        }

        // ── Harness ──────────────────────────────────────────────────────────────────────────

        private sealed class Harness
        {
            public readonly RecordingWorld World = new RecordingWorld();
            public readonly RoomParticleEmitter Emitter;
            public readonly SplitRng Rng;
            public readonly ScriptedRng Presentation;
            public readonly DamageSystem System;
            public readonly BleedingTarget Target = new BleedingTarget();

            /// <summary>
            /// A harness with a room pushed at the world origin, 25 tiles tall.
            /// </summary>
            /// <param name="bloodDraws">
            /// The scripted presentation-stream values, in the order the blood path draws them. Fewer
            /// than the path needs is legal — the remainder read as <c>0</c>.
            /// </param>
            public Harness(params float[] bloodDraws)
                : this(pushRoom: true, bloodDraws: bloodDraws)
            {
            }

            /// <summary>
            /// A harness with <b>no room pushed</b> — the adapter has no origin and no height, so it
            /// cannot place anything correctly and refuses. See
            /// <see cref="NoRoomPushed_ResolvesTheHitAndEmitsNothing"/>.
            /// </summary>
            public static Harness WithoutRoom(params float[] bloodDraws)
                => new Harness(pushRoom: false, bloodDraws: bloodDraws);

            private Harness(bool pushRoom, float[] bloodDraws)
            {
                Presentation = new ScriptedRng(bloodDraws);
                Emitter = new RoomParticleEmitter(World);

                if (pushRoom)
                {
                    Emitter.SetTileQuery(new StubTileQuery
                    {
                        RoomInstance = new RoomInstance { height = RoomHeight },
                        Origin = Vector2.zero,
                    });
                }

                Rng = new SplitRng(Presentation);

                PfeDebugSettings settings = MakeSettings();

                System = new DamageSystem(
                    new DamageCalculator(new CombatCalculator()),
                    Rng,
                    publisher: null,
                    settings,
                    new SimLoop(new SimClock(), settings),
                    Emitter);

                // Start() is what a container's IStartable would call. With SimTickEnabled off the
                // system still registers on the loop; Report() resolves inline because IsTickAligned
                // also requires the flag, so no SimTick is needed to see a hit applied.
                System.Start();
            }
        }

        /// <summary>
        /// The settings asset with the four gates this fixture depends on set explicitly. The fields are
        /// private and serialized, so they are set reflectively — the same way Unity's serializer would.
        /// </summary>
        /// <remarks>
        /// <c>testDamage: true</c> is the opposite of the asset's default, and it is what makes the
        /// expected damage an exact number: it leaves the spread's <i>draw</i> in place (so the combat
        /// stream advances as it does in production) but discards its <i>result</i>, so
        /// <c>baseDamage: 20</c> really does arrive as 20.
        /// </remarks>
        private static PfeDebugSettings MakeSettings()
        {
            var settings = OfflineScriptableObject.Create<PfeDebugSettings>();

            SetPrivateField(settings, "simTickEnabled", false);
            SetPrivateField(settings, "simTickDamage", false);
            SetPrivateField(settings, "applyVulnerabilities", false);
            SetPrivateField(settings, "testDamage", true);

            return settings;
        }

        private static void SetPrivateField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(
                name, BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(field, Is.Not.Null,
                $"PfeDebugSettings.{name} was renamed; update this helper");

            field.SetValue(target, value);
        }

        private static DamageContext Context(
            float baseDamage = 20f,
            DamageType damageType = DamageType.PhysicalBullet,
            Vector2 knockbackDir = default)
            => new DamageContext(
                owner: null,
                weapon: null,
                baseDamage: baseDamage,
                explosionDamage: 0f,
                armorMultiplier: 1f,
                piercing: 0f,
                knockback: 0f,
                knockbackDir: knockbackDir,
                critChance: 0f,
                critMultiplier: 1f,
                damageType: damageType,
                destroyTiles: 0f,
                penetrationChance: 0f,
                dopEffect: null,
                dopDamage: 0f,
                dopChance: 1f,
                missChance: 0f,
                precision: 0f,
                isMelee: false);

        // ── The bullet branch: two anchors in one hit ────────────────────────────────────────

        /// <summary>
        /// The whole wiring in one hit, because that is how the oracle does it: a bullet sprays from
        /// its own contact point <i>and</i> gibs from the target's origin, in the same call.
        /// </summary>
        /// <remarks>
        /// <para>The draws, in the oracle's order (<c>Unit.as:3863-3899</c>): the bullet-path count
        /// jitter, the gib's probability roll, then — because this hit has a bullet — no coin flip for
        /// the throw direction, then the variant, the lateral jitter and the drop. Five draws, scripted
        /// below so every intermediate is a known number.</para>
        /// </remarks>
        [Test]
        public void BulletHit_SpraysFromTheContactPointAndGibsFromTheTarget()
        {
            // 0.5 → kol = floor(0.5 * 5 + 20 / 5) = 6
            // 0.0 → the gib procs (20 / 1000 > 0.0)
            // 0.5 → variant = 1 + floor(0.5 * 3) = 2
            // 0.5 → lateral = (0.5 - 0.5) * 40 * 0.5 = 0
            // 0.0 → drop = 0.0 * 60 * 0.5 = 0
            var h = new Harness(0.5f, 0.0f, 0.5f, 0.5f, 0.0f);

            // Unity (5.5, 2.2) = world pixel (550, 220) = 780 px above this room's floor.
            var bulletPos = new Vector3(5.5f, 2.2f, 0f);

            DamageVerdict verdict = h.System.Report(PendingDamage.Direct(
                Context(knockbackDir: new Vector2(1f, 0.5f)),
                h.Target,
                h.Target.Origin,
                travelDistancePixels: 300f,
                bulletPosition: bulletPos));

            Assert.AreEqual(DamageVerdict.Landed, verdict);

            // The premise every number below depends on. Asserted rather than assumed, so a formula
            // change fails here — where it says what broke — instead of inside a Kol comparison.
            Assert.AreEqual(20f, h.Target.LastOutcome.HpDamage, 0.001f,
                "premise: no armour, no crit, no vulnerabilities and testDamage on means the hit " +
                "arrives as exactly baseDamage");

            Assert.AreEqual(2, h.World.Emits.Count, "the spray and the gib");

            // ── The spray, at the round's contact point ──────────────────────────────────────
            (string sprayId, float sprayX, float sprayY, ParticleSpec spraySpec) = h.World.Emits[0];

            Assert.AreEqual("blood", sprayId, "red blood → Emitter.arr[\"blood\"] (Unit.as:3850)");
            Assert.AreEqual(550f, sprayX, 0.001f, "the bullet's contact X, not the unit's");
            Assert.AreEqual(780f, sprayY, 0.001f, "the bullet's contact Y, not the unit's");

            // Both controls are the two anchors this hit must NOT use for the spray. Without them the
            // assertions above would also pass for a spray dropped from the target.
            Assert.AreNotEqual(500f, sprayX, "the spray used the unit anchor instead of the bullet's");
            Assert.AreNotEqual(770f, sprayY, "the spray used the standing mid-height anchor");

            Assert.AreEqual(6, spraySpec.Kol, "floor(0.5 * 5 + 20 / 5)");
            Assert.AreEqual(5f, spraySpec.DX, 0.001f, "directionX * 5 (Unit.as:3868)");

            // The Y mirror. Unity's direction is (1, 0.5) — up and to the right — and AS3's Y runs
            // down, so the oracle's component is -0.5 and the throw is -2.5. Forwarded verbatim it
            // would be +2.5, i.e. blood thrown into the ceiling.
            Assert.AreEqual(-2.5f, spraySpec.DY, 0.001f, "the direction's Y was not mirrored");

            // ── The gib, from the target's origin even though the hit had a bullet ───────────
            (string gibId, float gibX, float gibY, ParticleSpec gibSpec) = h.World.Emits[1];

            Assert.AreEqual("bloodexpl2", gibId, "variant 2 of bloodexpl1..3 (Unit.as:3897)");
            Assert.AreEqual(580f, gibX, 0.001f, "unit X 500 + 80 * throwSign (Unit.as:3897)");
            Assert.AreEqual(760f, gibY, 0.001f, "unit Y 800 - drop 0 - 40 (Unit.as:3897)");
            Assert.IsFalse(gibSpec.Mirr, "throwSign is +1, so the sprite is not mirrored");
        }

        // ── The standing branch: melee ───────────────────────────────────────────────────────

        /// <summary>
        /// A melee hit drops the spray from the target's mid-height with <b>no</b> velocity override —
        /// AS3 <c>this.bloodEmit.cast(loc, X, Y - scY / 2, {"kol": …})</c> (<c>Unit.as:3873</c>).
        /// </summary>
        /// <remarks>
        /// This is the branch AS3's melee takes, and the reason <c>PendingDamage.HasBullet</c> is its
        /// own field rather than a negation of <c>ReachedDamageWithoutUdarBullet</c>:
        /// <c>udarUnit</c> calls <c>damage()</c> with <b>two</b> arguments (<c>:4162</c>), so
        /// <c>param3</c> is null and a sword draws the standing shape.
        /// </remarks>
        [Test]
        public void MeleeHit_DropsTheSprayFromTheTargetsMidHeightWithNoVelocity()
        {
            // One draw: the gib's probability roll, which is declined at 0.99 (20 / 1000 is not > 0.99).
            var h = new Harness(0.99f);

            DamageVerdict verdict = h.System.Report(PendingDamage.Direct(
                Context(damageType: DamageType.PhysicalMelee),
                h.Target,
                h.Target.Origin));

            Assert.AreEqual(DamageVerdict.Landed, verdict);
            Assert.AreEqual(20f, h.Target.LastOutcome.HpDamage, 0.001f, "premise");

            Assert.AreEqual(1, h.World.Emits.Count, "no gib at this damage, so the spray alone");

            (string id, float x, float y, ParticleSpec spec) = h.World.Emits[0];

            Assert.AreEqual("blood", id);
            Assert.AreEqual(500f, x, 0.001f, "the unit's own X");
            Assert.AreEqual(770f, y, 0.001f, "unit Y 800 - scY / 2 = 800 - 30");

            // Control: the bullet anchor is a different Y, so a wiring that always used the contact
            // point would be caught here rather than looking like a slightly-off spray.
            Assert.AreNotEqual(780f, y, "the standing spray used the bullet anchor");

            Assert.AreEqual(6, spec.Kol, "floor(20 / 3) — the standing divisor, not the bullet's /5");
            Assert.AreEqual(0f, spec.DX, 0.001f, "no velocity override on this branch");
            Assert.AreEqual(0f, spec.DY, 0.001f, "no velocity override on this branch");
        }

        // ── The gate: what a hit that draws no blood must NOT cost ───────────────────────────

        /// <summary>
        /// A non-bleeding damage type emits nothing — and, just as importantly, <b>consumes no draw</b>.
        /// The gate sits above every draw in <see cref="BloodSprayRules.Plan"/>, which is what makes
        /// that true.
        /// </summary>
        [Test]
        public void NonBleedingDamageType_EmitsNothingAndTakesNoDraw()
        {
            var h = new Harness(0.5f, 0.5f, 0.5f, 0.5f, 0.5f);

            // Fire is not in AS3's list (Unit.as:3844): only D_BUL, D_BLADE, D_PHIS, D_BLEED and
            // D_FANG bleed. A flamethrower draws no spray, and a grenade's blast draws none either.
            h.System.Report(PendingDamage.Direct(
                Context(damageType: DamageType.Fire), h.Target, h.Target.Origin));

            Assert.AreEqual(0, h.World.Emits.Count);
            Assert.AreEqual(0, h.Presentation.RollsConsumed,
                "the gate must sit above the draws, or a fire hit would shift the gib rolls of the " +
                "next bleeding hit in the same tick");

            // Positive control: the identical hit with a bleeding type does emit, so the assertion
            // above is about the damage type and not about the harness being inert.
            var control = new Harness(0.99f);
            control.System.Report(PendingDamage.Direct(
                Context(damageType: DamageType.PhysicalBullet), control.Target, control.Target.Origin));

            Assert.AreEqual(1, control.World.Emits.Count);
        }

        /// <summary>
        /// A target that does not bleed — AS3 <c>blood == 0</c>, 100 of the 134 units — emits nothing.
        /// </summary>
        [Test]
        public void BloodlessTarget_EmitsNothing()
        {
            var h = new Harness(0.5f);
            h.Target.Blood = BloodType.None;

            h.System.Report(PendingDamage.Direct(
                Context(), h.Target, h.Target.Origin));

            Assert.AreEqual(0, h.World.Emits.Count);
            Assert.AreEqual(0, h.Presentation.RollsConsumed, "the gate is above the draws");

            // Positive control.
            var control = new Harness(0.99f);
            control.System.Report(PendingDamage.Direct(
                Context(), control.Target, control.Target.Origin));

            Assert.AreEqual(1, control.World.Emits.Count);
        }

        /// <summary>
        /// A damageable that is not a unit — a crate, a mine — has no blood to spray, and the oracle
        /// agrees: the block is a <c>Unit</c> behaviour, so a prop impact never reaches it.
        /// </summary>
        [Test]
        public void TargetThatIsNotAUnit_EmitsNothing()
        {
            var h = new Harness(0.5f);
            var crate = new PlainTarget();

            h.System.Report(PendingDamage.Direct(Context(), crate, UnitOrigin));

            Assert.AreEqual(0, h.World.Emits.Count);
            Assert.AreEqual(0, h.Presentation.RollsConsumed,
                "the cast fails before Plan, so nothing is drawn either");

            // Positive control: the same hit on a unit that does implement the interface.
            var control = new Harness(0.99f);
            control.System.Report(PendingDamage.Direct(
                Context(), control.Target, control.Target.Origin));

            Assert.AreEqual(1, control.World.Emits.Count);
        }

        // ── The refusal ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// With no room pushed there is no origin and no height, so there is no correct place to put the
        /// particles. The adapter refuses and the hit still resolves — losing the visual must not cost
        /// the damage.
        /// </summary>
        [Test]
        public void NoRoomPushed_ResolvesTheHitAndEmitsNothing()
        {
            var h = Harness.WithoutRoom(0.5f, 0.0f, 0.5f, 0.5f, 0.0f);

            DamageVerdict verdict = h.System.Report(PendingDamage.Direct(
                Context(), h.Target, h.Target.Origin));

            Assert.AreEqual(DamageVerdict.Landed, verdict, "damage must not depend on the view");
            Assert.AreEqual(20f, h.Target.LastOutcome.HpDamage, 0.001f);
            Assert.AreEqual(0, h.World.Emits.Count);
            Assert.AreEqual(0, h.Presentation.RollsConsumed,
                "the conversion is refused before Plan, so no blood draw is taken");

            // Positive control — the same hit with a room pushed emits, so the assertion above is about
            // the missing room and not about the harness being inert.
            var control = new Harness(0.99f);
            control.System.Report(PendingDamage.Direct(
                Context(), control.Target, control.Target.Origin));

            Assert.AreEqual(1, control.World.Emits.Count);
        }

        // ── The stream split ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The blood draws come from the <b>presentation</b> stream, and the count proves it: the
        /// scripted presentation RNG records five draws for the hit below, and it would record zero if
        /// the code had drawn from the combat stream instead.
        /// </summary>
        /// <remarks>
        /// This is a deliberate divergence from the oracle, which has one global <c>Math.random()</c>
        /// and therefore lets a <i>visual</i> shift the knockback roll <c>otbros</c> takes a few lines
        /// later. The port cannot keep per-stream determinism and reproduce that at the same time, and
        /// should not want to. The assertion is on the count rather than on a value because the count is
        /// the thing that would move.
        /// </remarks>
        [Test]
        public void TheBloodDrawsComeFromThePresentationStreamNotTheCombatStream()
        {
            var h = new Harness(0.5f, 0.0f, 0.5f, 0.5f, 0.0f);

            h.System.Report(PendingDamage.Direct(
                Context(knockbackDir: new Vector2(1f, 0.5f)),
                h.Target,
                h.Target.Origin,
                travelDistancePixels: 300f,
                bulletPosition: new Vector3(5.5f, 2.2f, 0f)));

            // kol jitter, gib probability, variant, lateral, drop.
            Assert.AreEqual(5, h.Presentation.RollsConsumed,
                "the blood path must take all five of its draws from the presentation stream");

            // And the split is exactly one request each: the combat stream for the resolver, the
            // presentation stream for the blood. If the blood path ever went back to the combat stream
            // the second request would disappear and the count above would read 0.
            Assert.AreEqual(2, h.Rng.Requests.Count);
            Assert.AreEqual(1, Requests(h.Rng, RngStream.Combat),
                "one request for the combat stream — the immediate-mode resolver's");
            Assert.AreEqual(1, Requests(h.Rng, RngStream.Presentation),
                "one request for the presentation stream — the blood path's");

            foreach (var request in h.Rng.Requests)
                Assert.IsNull(request.Salt, "immediate mode has no tick index to salt with");

            // Both streams are resolved once and memoized: GetStream builds a fresh generator per call,
            // so fetching per hit would hand every hit an identical sequence.
            h.System.Report(PendingDamage.Direct(
                Context(knockbackDir: new Vector2(1f, 0.5f)),
                h.Target,
                h.Target.Origin,
                travelDistancePixels: 300f,
                bulletPosition: new Vector3(5.5f, 2.2f, 0f)));

            Assert.AreEqual(2, h.Rng.Requests.Count,
                "neither stream may be re-resolved per hit");
            Assert.AreEqual(10, h.Presentation.RollsConsumed,
                "the second hit draws another five from the SAME generator, which is what makes the " +
                "sequence continue rather than repeat");
        }

        /// <summary>How many times <paramref name="stream"/> was asked for.</summary>
        private static int Requests(SplitRng rng, RngStream stream)
        {
            int count = 0;

            foreach (var request in rng.Requests)
            {
                if (request.Stream == stream) count++;
            }

            return count;
        }
    }
}
