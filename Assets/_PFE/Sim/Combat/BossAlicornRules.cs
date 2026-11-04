using System;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// The whole of AS3 <c>UnitBossAlicorn</c>'s decision layer, as pure functions —
    /// <c>fe/unit/UnitBossAlicorn.as:13-752</c>.
    ///
    /// <para><b>Why this class exists at all, when the family already has
    /// <see cref="AlicornVariantRules"/>.</b> <c>UnitBossAlicorn extends UnitPon</c>
    /// (<c>:13</c>), not <c>UnitAlicorn</c>. It shares the alicorn's <i>identity</i> — a flyer with
    /// magic bolts and a shield — and none of its <i>code</i>: no <c>tr</c> table, no
    /// <c>superSila</c>, no <c>osob</c>, no <c>actPort</c>, no <c>findBox</c> cycle. Its fight is a
    /// fixed <b>timer rotation</b> (<c>aiState</c> 0..3 on <c>aiTCh</c>), a <b>seven-point teleport
    /// grid</b>, and a <b>random attack-state selector</b> (<c>attState</c> 0..5) — none of which any
    /// family row expresses. Putting these numbers in <c>AlicornVariantRules</c> would invite exactly
    /// the mistake that table already caused once: the <c>tr1</c>-gets-the-<c>tr3</c>-shield bug
    /// (<c>AlicornController.Tier</c>'s remarks).</para>
    ///
    /// <para><b>Unity-free on purpose.</b> A <c>MonoBehaviour</c> cannot be constructed in the offline
    /// harness, so a fixture that drove <c>BossAlicornBrain</c> directly could not run there, and a
    /// guard that cannot run is decoration. Everything here is arithmetic and gates; the brain reads
    /// these and performs the side effects.</para>
    ///
    /// <para><b>What is deliberately NOT here.</b> HP, damage, sprite rows and the sheet are imported
    /// data and live on the <c>.asset</c>. The one exception is
    /// <see cref="HpAfterShieldBreak"/>, because <c>hp = maxhp - 0.1</c> is a <i>rule</i> about the
    /// shield break rather than a stat — it is why the second stage starts one tenth of a point below
    /// full rather than at half.</para>
    ///
    /// <para><b>Y is mirrored.</b> AS3's Y grows down; every pixel value this class returns is in the
    /// port's y-up room-local space. Only <see cref="FloatBob"/> has a sign that had to be carried
    /// across, and it says so.</para>
    /// </summary>
    public static class BossAlicornRules
    {
        // ── constructor literals (UnitBossAlicorn.as:16-139) ──────────────────────────────────────

        /// <summary><c>aiTCh = 80</c> (<c>:118</c>) — the first rotation step, ~2.7 s at 30 Hz.</summary>
        public const int InitialAiTCh = 80;

        /// <summary>
        /// <c>mp = 3</c> (<c>:95</c>) — the move point the boss starts on. It matters: the first
        /// teleport re-rolls when it lands on the current index (<c>:406</c>), so this initial value is
        /// what the first step is guaranteed <i>not</i> to pick.
        /// </summary>
        public const int InitialMovePoint = 3;

        /// <summary><c>t_turn = 15</c> (<c>:103</c>) — ticks the facing takes to follow the target.</summary>
        public const int TurnTicks = 15;

        /// <summary>
        /// <c>t_fly = 3</c> (<c>:107</c>) — grounded ticks before the boss lifts off again
        /// (<c>:378-389</c>). Note this is <b>three</b>, not the family alicorn's seven
        /// (<c>AlicornBrain.FallTicksBeforeLift</c>): the boss is a hoverer, not a hopper.
        /// </summary>
        public const int FlyTicks = 3;

        /// <summary>
        /// <c>t_shit = 300</c> (<c>:105</c>). <b>Decremented and never read</b> — <c>:374-376</c> is
        /// its only reader, and it is a decrement. Kept because a reader of the oracle needs to see
        /// that the shield timer is <i>vestigial</i> rather than missing.
        /// </summary>
        public const int InitialShieldTimer = 300;

        /// <summary>
        /// <c>shitMaxHp = 400</c> (<c>:32</c>) and <c>shitArmor = 15</c> (<c>:119</c>) — also
        /// vestigial: <b>nothing in the class ever assigns <c>shithp</c></b>, and <c>Unit.damage()</c>
        /// reaches <c>shitArmor</c> only inside <c>if(shithp &gt; 0)</c>. The boss absorbs nothing; the
        /// dome is a one-shot on death. See <see cref="HpAfterShieldBreak"/>.
        /// </summary>
        public const float ShieldMaxHp = 400f;

        /// <summary>See <see cref="ShieldMaxHp"/>.</summary>
        public const float ShieldArmor = 15f;

        /// <summary><c>timerDie = 90</c> (<c>:138</c>) — the death sequence's length, used by
        /// <c>setVisPos</c>'s shake (<c>:282-283</c>) and reset by the shield break (<c>:726</c>).</summary>
        public const int DeathTimerTicks = 90;

        /// <summary><c>aiState &gt; 3 → 1</c> (<c>:398-401</c>). The rotation has three live steps;
        /// <b>0</b> is the spawn state and is never returned to.</summary>
        public const int MaxAiState = 3;

        /// <summary><c>Math.floor(Math.random() * 5)</c> (<c>:404</c>) — <c>attState</c> 0..4, with
        /// <b>5</b> reachable only through the difficulty roll (<c>:417-420</c>).</summary>
        public const int AttackStateCount = 5;

        /// <summary><c>attState == 5</c> — <c>alimray</c>, the sustained beam.</summary>
        public const int BeamAttackState = 5;

        /// <summary><c>attState == 3</c> — the telekinetic grab-then-throw (<c>:444-451</c>).</summary>
        public const int TelekinesisAttackState = 3;

        /// <summary><c>attState == 4</c> — <c>superInvis</c> plus the scythe emission (<c>:452-456</c>).</summary>
        public const int InvisibilityAttackState = 4;

        /// <summary><c>weaps = [alilight2, aliblade, alipsy2, alimray]</c> (<c>:128</c>) — the three
        /// ranged states index <c>weaps[attState]</c> directly, which is why
        /// <c>attState &lt;= 2</c> is the weapon gate.</summary>
        public const int RangedWeaponCount = 3;

        /// <summary><c>while(_loc2_ &lt; 6) emit(_loc2_++)</c> (<c>:727-732</c>) — six bound scythes
        /// appear when the dome shatters.</summary>
        public const int ScythesOnShieldBreak = 6;

        /// <summary>
        /// <c>loc.createUnit("scythe", Math.random() * 1600 + 160, Math.random() * 500 + 100, true)</c>
        /// (<c>:296</c>) — the spawn box, in <b>AS3 room-local pixels</b> (x from the left, y from the
        /// ceiling).
        /// </summary>
        /// <remarks>
        /// The box is <b>not</b> the arena: x 160..1760 spans most of a 1920-px-wide room and y
        /// 100..600 is the top third of a 1000-px-tall one, i.e. the blades materialise around and above
        /// the boss's own seven points. <see cref="ScytheSpawnPixels"/> mirrors the Y; a port that used
        /// the literal would drop all six into the floor.
        /// </remarks>
        public const float ScytheSpawnMinXPixels = 160f;

        /// <summary>See <see cref="ScytheSpawnMinXPixels"/> — <c>1600 + 160</c>.</summary>
        public const float ScytheSpawnMaxXPixels = 1760f;

        /// <summary>See <see cref="ScytheSpawnMinXPixels"/> — <c>100</c> from the ceiling.</summary>
        public const float ScytheSpawnMinYAs3Pixels = 100f;

        /// <summary>See <see cref="ScytheSpawnMinXPixels"/> — <c>500 + 100</c> from the ceiling.</summary>
        public const float ScytheSpawnMaxYAs3Pixels = 600f;

        /// <summary>
        /// One <c>emit()</c> spawn point in <b>room-local, y-up</b> pixels.
        /// </summary>
        /// <param name="rollX">Uniform draw for x; the caller's <c>Math.random()</c>.</param>
        /// <param name="rollY">Uniform draw for y.</param>
        /// <param name="roomHeightTiles">The live room's height, for the Y mirror.</param>
        public static Vector2Pixels ScytheSpawnPixels(float rollX, float rollY, int roomHeightTiles = CanonicalRoomHeightTiles)
        {
            if (roomHeightTiles <= 0)
            {
                roomHeightTiles = CanonicalRoomHeightTiles;
            }

            float x = ScytheSpawnMinXPixels + Clamp01(rollX) * (ScytheSpawnMaxXPixels - ScytheSpawnMinXPixels);
            float as3Y = ScytheSpawnMinYAs3Pixels
                         + Clamp01(rollY) * (ScytheSpawnMaxYAs3Pixels - ScytheSpawnMinYAs3Pixels);

            // AS3 y-down → port y-up, the same mirror the move points take.
            return new Vector2Pixels(x, roomHeightTiles * MovePointTileSize - as3Y);
        }

        /// <summary>
        /// A pair of room-local pixel coordinates. A tiny struct rather than <c>Vector2</c> so this
        /// class keeps compiling in the offline harness, which links the rules and no Unity types.
        /// </summary>
        public readonly struct Vector2Pixels
        {
            public readonly float X;
            public readonly float Y;

            public Vector2Pixels(float x, float y)
            {
                X = x;
                Y = y;
            }
        }

        static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        /// <summary>
        /// <c>if(param1 &gt;= 0) { _loc2_.bind = this; _loc2_.maxhp *= 8; _loc2_.hp = maxhp;
        /// (_loc2_ as UnitScythe).bindN = param1; }</c> (<c>:300-306</c>) — a bound blade is eight times
        /// as tough as a free one.
        /// </summary>
        public const float BoundScytheHealthMultiplier = 8f;

        /// <summary><c>_loc2_.dam *= 1 + level * 0.1</c> (<c>:298</c>) — the emit's own damage scale.</summary>
        public static float ScytheDamageScale(float level) => 1f + level * 0.1f;

        /// <summary><c>if(attState == 4 &amp;&amp; aiTCh % 30 == 1) emit()</c> (<c>:580</c>) — a seventh
        /// scythe, unbound, every thirty ticks of the invisibility step.</summary>
        public const int ScytheEmitInterval = 30;

        /// <summary><c>dist2 &lt; 100 * 100</c> (<c>:584</c>) — the contact-attack reach, in pixels.</summary>
        public const float ContactAttackRangePixels = 100f;

        /// <summary><c>isrnd(0.1)</c> (<c>:584</c>) — the per-tick contact-attack roll.</summary>
        public const float ContactAttackChance = 0.1f;

        // ── telekinesis (UnitBossAlicorn.as:46-70, :591-701) ──────────────────────────────────────

        /// <summary><c>teleSpeed = 24</c> (<c>:54</c>) — the held object's per-axis velocity cap.</summary>
        public const float TeleSpeed = 24f;

        /// <summary><c>teleAccel = 6</c> (<c>:56</c>) — the per-tick nudge.</summary>
        public const float TeleAccel = 6f;

        /// <summary><c>derp = 15</c> (<c>:58</c>) — the hold deadzone, same as the family's.</summary>
        public const float HoldDeadzone = 15f;

        /// <summary><c>throwForce = 60</c> (<c>:48</c>) — <b>twice the <c>tr2</c> alicorn's 45</b>.
        /// The boss's throw is the hardest in the game.</summary>
        public const float ThrowForce = 60f;

        /// <summary><c>optDistTele = 1000</c> (<c>:60</c>) — 1000 px, against the family's 600.</summary>
        public const float TeleRangePixels = 1000f;

        /// <summary><c>if(celUnit &amp;&amp; isrnd(0.5))</c> (<c>:594</c>) — the boss prefers the
        /// <b>player</b> on a coin flip, twice the family alicorn's 25 %.</summary>
        public const float UnitGrabChance = 0.5f;

        /// <summary><c>isrnd(0.3)</c> per prop (<c>:606</c>) — the same rate as the family's.</summary>
        public const float PropGrabChance = 0.3f;

        /// <summary>
        /// <c>if(teleObj.X &lt; 200 || teleObj.X &gt; loc.limX - 200) _loc2_ *= 0.6</c>
        /// (<c>:668-671</c>) — the throw is damped near either wall so a victim is not fired through
        /// the room's edge.
        ///
        /// <para><b>The family alicorn has no such term.</b> It is the boss's alone, which is why it is
        /// a parameter of <see cref="ThrowSpeed"/> rather than a rule inside
        /// <c>AlicornVariantRules.ThrowSpeed</c>.</para>
        /// </summary>
        public const float NearWallThrowFactor = 0.6f;

        /// <summary>The distance from a room edge that counts as "near the wall" (<c>:668</c>).</summary>
        public const float NearWallMarginPixels = 200f;

        // ── the float bob (UnitBossAlicorn.as:489-498) ────────────────────────────────────────────

        /// <summary><c>t_float += 0.0726</c> (<c>:491</c>) — the phase step. 0.0726 rad/tick is a
        /// ~86-tick period at 30 Hz, i.e. about 2.9 s per bob.</summary>
        public const float FloatPhaseStep = 0.0726f;

        /// <summary><c>Math.sin(t_float) * 2</c> / <c>Math.cos(t_float) * 2</c> (<c>:492-493</c>) — the
        /// bob's amplitude, in pixels per frame.</summary>
        public const float FloatAmplitude = 2f;

        /// <summary><c>dx *= 0.9; dy *= 0.9;</c> (<c>:494-495</c>) — the per-tick damping the bob is
        /// added on top of. This is the boss's whole locomotion: it never walks.</summary>
        public const float FloatDamping = 0.9f;

        // ── the seven move points (UnitBossAlicorn.as:72-93) ──────────────────────────────────────

        /// <summary><c>movePoints.length</c> — seven authored points, in <b>tiles</b>.</summary>
        public const int MovePointCount = 7;

        /// <summary>
        /// <c>movePoints</c> (<c>:72-93</c>), verbatim, in tiles. The arena they describe is the
        /// Manehattan boss room: x 7..42, y 5..15, a wide V around the centre dais.
        /// </summary>
        /// <remarks>
        /// <b>These are the only places the boss can be.</b> <c>teleport()</c> is called from exactly
        /// one place (<c>:415</c>) and always with a move point, so the boss's position set is seven
        /// points and the teleport line between them. A port that teleports "somewhere near the
        /// player" is a different fight — and that is what this port used to do.
        /// </remarks>
        static readonly int[] MovePointTileX = { 24, 8, 40, 7, 42, 11, 38 };

        /// <summary>See <see cref="MovePointTileX"/>.</summary>
        static readonly int[] MovePointTileY = { 5, 7, 7, 11, 11, 15, 15 };

        /// <summary>The tile size the oracle hard-codes as the literal <c>40</c> (<c>:415</c>).</summary>
        public const float MovePointTileSize = 40f;

        /// <summary>
        /// <c>World.cellsY = 25</c> (<c>World.as:39</c>) — the room height the seven points are
        /// authored against, and the fallback when a caller has no live room.
        ///
        /// <para>A local literal rather than a reference to <c>TileQueryConstants.RoomCellsY</c>, for
        /// the same reason <see cref="MovePointTileSize"/> is one: this class has to compile in the
        /// offline harness, which links the rules and nothing else, and <c>TileQueryConstants</c>
        /// drags in <c>PFE.Core.SimClock</c>. The value is asserted equal to the physics constant by
        /// the harness, so the duplication cannot drift silently.</para>
        /// </summary>
        public const int CanonicalRoomHeightTiles = 25;

        /// <summary>
        /// <c>movePoints[mp].x * 40 + 20</c> (<c>:415</c>) — the teleport target's room-local X in
        /// pixels. The <c>+20</c> is the half-tile that centres the body on the tile.
        /// </summary>
        public static float MovePointPixelX(int index)
        {
            if (index < 0 || index >= MovePointCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return MovePointTileX[index] * MovePointTileSize + 20f;
        }

        /// <summary>
        /// <c>movePoints[mp].y * 40 + 40</c> (<c>:415</c>) — the room-local Y in pixels, <b>mirrored</b>
        /// into the port's y-up space.
        ///
        /// <para><b>This is the bug that made the boss teleport into the floor.</b> AS3's Y grows
        /// <i>down</i>, so <c>tileY * 40 + 40</c> measures from the room's <b>ceiling</b>. The port's
        /// room-local Y grows <i>up</i> from the floor, so the same number is the vertical <b>mirror</b>
        /// of the authored point — the seven points land in the lower half of the arena instead of the
        /// upper half. An earlier revision argued the opposite ("<c>setPos</c> takes the bottom in both
        /// engines, so nothing flips"), which is true of the <i>anchor</i> and false of the <i>axis</i>:
        /// both engines store the bottom, but one measures it from the ceiling and the other from the
        /// floor. The symptom is exactly what a player reports — the boss reappears at the wrong height,
        /// sometimes inside the dais, with no room to circle.</para>
        ///
        /// <para>The conversion is <see cref="WorldCoordinates.As3YToUnityLocalY"/>'s rule,
        /// <c>H*40 - y</c>, applied here rather than at the call site so the arithmetic stays in the
        /// Unity-free layer and the fixture can pin the literals. <c>+40</c> is kept: it is the bottom
        /// edge of the row in AS3 space (one row further down than the tile's origin), and the mirror
        /// carries it.</para>
        /// </summary>
        /// <param name="index">Index into the seven authored points.</param>
        /// <param name="roomHeightTiles">
        /// The live room's height in tiles — <c>RoomInstance.height</c>. Defaults to the port's
        /// canonical 25-row room, which is what the fixtures pin.
        /// </param>
        public static float MovePointPixelY(int index, int roomHeightTiles = CanonicalRoomHeightTiles)
        {
            if (index < 0 || index >= MovePointCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            if (roomHeightTiles <= 0)
            {
                roomHeightTiles = CanonicalRoomHeightTiles;
            }

            // AS3 room-local Y (down, 0 = ceiling) → port room-local Y (up, 0 = floor).
            float as3Y = MovePointTileY[index] * MovePointTileSize + 40f;
            return roomHeightTiles * MovePointTileSize - as3Y;
        }

        // ── the rotation ladder (UnitBossAlicorn.as:391-477) ──────────────────────────────────────

        /// <summary>
        /// <c>++aiState; if(aiState &gt; 3) aiState = 1;</c> (<c>:397-401</c>).
        ///
        /// <para><b>It wraps to 1, not 0.</b> State 0 is the spawn state — the boss's constructor leaves
        /// <c>aiState</c> at 0 and it advances on the first <c>aiTCh</c> expiry. A port that wrapped to 0
        /// would give the boss an extra "do nothing" step every fourth beat.</para>
        /// </summary>
        public static int NextAiState(int aiState)
        {
            int next = aiState + 1;
            return next > MaxAiState ? 1 : next;
        }

        /// <summary>
        /// <c>this.attState = Math.floor(Math.random() * 5)</c> (<c>:404</c>) — 0..4.
        ///
        /// <para><b>Not 0..5.</b> The port used <c>Random.Range(0, 6)</c>, which made the beam
        /// (<c>attState 5</c>) a permanent 1-in-6 option instead of a difficulty-gated one — the beam is
        /// supposed to be the reward for playing on hard, not a coin flip on normal.</para>
        /// </summary>
        public static int RollAttackState(float roll)
        {
            int state = (int)Math.Floor(roll * AttackStateCount);
            if (state < 0) return 0;
            return state >= AttackStateCount ? AttackStateCount - 1 : state;
        }

        /// <summary>
        /// <c>if(globalDif == 4 &amp;&amp; isrnd(0.2) || globalDif == 3 &amp;&amp; isrnd(0.1)) attState = 5</c>
        /// (<c>:417-420</c>) — the beam is <b>difficulty-gated</b>, 20 % on the hardest setting and
        /// 10 % one below, and impossible otherwise.
        /// </summary>
        /// <remarks>
        /// <b>One roll, two thresholds — not two rolls.</b> AS3's <c>||</c> short-circuits: at
        /// <c>dif 4</c> the second operand is never evaluated, so the oracle draws <i>one</i> number
        /// here and the caller must do the same or the RNG stream shifts.
        /// </remarks>
        public static bool UpgradesToBeam(int difficulty, float roll)
        {
            if (difficulty >= 4) return roll < 0.2f;
            if (difficulty == 3) return roll < 0.1f;
            return false;
        }

        /// <summary>
        /// <c>_loc4_ = Math.floor(Math.random() * 7); if(_loc4_ == mp) _loc4_++; if(_loc4_ &gt;= 7)
        /// _loc4_ = 0; mp = _loc4_;</c> (<c>:405-414</c>).
        ///
        /// <para><b>The boss never teleports to where it already is.</b> The skip is a single increment,
        /// so it re-lands on index 0 rather than 1 when the current point is 6 — the wrap is the
        /// oracle's, not a modulo.</para>
        /// </summary>
        public static int RollMovePoint(int current, float roll)
        {
            int next = (int)Math.Floor(roll * MovePointCount);
            if (next < 0) next = 0;
            if (next >= MovePointCount) next = MovePointCount - 1;

            if (next == current)
            {
                next++;
            }

            return next >= MovePointCount ? 0 : next;
        }

        /// <summary>
        /// The <c>aiTCh</c> set by the <c>aiState == 1</c> arm (<c>:416</c>) — always 30, after the
        /// teleport. The beam upgrade happens <i>after</i> this assignment and does not change it.
        /// </summary>
        public const int AiTChAfterTeleport = 30;

        /// <summary>
        /// The <c>aiState == 2</c> arm (<c>:441-456</c>): <c>aiTCh = 30</c>, then 45/15 for the grab
        /// (15 when <c>findBox()</c> found nothing) and 20 for the invisibility step.
        /// </summary>
        /// <param name="grabbed">
        /// <c>findBox() != null</c> — the result of the grab attempt this tick. Only read for
        /// <see cref="TelekinesisAttackState"/>.
        /// </param>
        public static int AiTChForState2(int attState, bool grabbed)
        {
            if (attState == TelekinesisAttackState)
            {
                // `aiTCh = 45; if(!this.findBox()) aiTCh = 15;` — a failed grab retries quickly,
                // because the boss has nothing to throw and would otherwise idle for 1.5 s.
                return grabbed ? 45 : 15;
            }

            if (attState == InvisibilityAttackState)
            {
                return 20;
            }

            return 30;
        }

        /// <summary>
        /// The <c>aiState == 3</c> arm (<c>:457-476</c>): 30 after a throw, 120 after the invisibility
        /// step, 75 after a beam, and 50..149 after a plain shot.
        /// </summary>
        /// <param name="roll">A uniform draw in <c>[0,1)</c>, used only by the default arm.</param>
        public static int AiTChForState3(int attState, float roll)
        {
            switch (attState)
            {
                case TelekinesisAttackState:
                    return 30;
                case InvisibilityAttackState:
                    return 120;
                case BeamAttackState:
                    return 75;
                default:
                    return 50 + (int)Math.Floor(roll * 100f);
            }
        }

        /// <summary>
        /// The aim gate, <c>:478</c>, with AS3's precedence made explicit:
        /// <code>
        /// ((aiState == 1 || (aiState &gt; 1 &amp;&amp; attState &lt;= 2)) &amp;&amp; aiTCh % 10 == 1)
        ///   || (attState == 5 &amp;&amp; aiState == 3)
        /// </code>
        ///
        /// <para><b>The <c>aiTCh % 10 == 1</c> term is a cadence, not a phase.</b> <c>aiTCh</c> counts
        /// <i>down</i> from 30/45/50..149, so <c>% 10 == 1</c> fires three times per 30-tick beat —
        /// at 21, 11 and 1. Aiming every tick instead would re-point the weapon every frame and make the
        /// boss track a strafing player perfectly, which the oracle does not do.</para>
        /// </summary>
        public static bool ShouldAim(int aiState, int attState, int aiTCh)
        {
            bool rangedStep = aiState == 1 || (aiState > 1 && attState <= 2);
            if (rangedStep && aiTCh % 10 == 1)
            {
                return true;
            }

            return attState == BeamAttackState && aiState == 3;
        }

        /// <summary>
        /// <c>if(aiState == 3) { if(attState &lt;= 2 || attState == 5) currentWeapon.attack(); }</c>
        /// (<c>:574-579</c>) — weapons fire only on the <b>last</b> beat of the rotation, and never on
        /// the grab or the invisibility step.
        /// </summary>
        public static bool ShouldFire(int aiState, int attState)
        {
            return aiState == 3 && (attState <= 2 || attState == BeamAttackState);
        }

        /// <summary>
        /// <c>if(attState == 4 &amp;&amp; aiTCh % 30 == 1) emit()</c> (<c>:580-583</c>) — the unbound
        /// scythe every thirty ticks of the invisibility step, i.e. four of them over the 120-tick beat.
        /// </summary>
        public static bool ShouldEmitScythe(int aiState, int attState, int aiTCh)
        {
            return aiState == 3 && attState == InvisibilityAttackState && aiTCh % ScytheEmitInterval == 1;
        }

        /// <summary>
        /// <c>if(attState != 4 &amp;&amp; dist2 &lt; 100 * 100 &amp;&amp; isrnd(0.1)) attKorp(celUnit, 0.5)</c>
        /// (<c>:584-587</c>) — a contact hit for half damage, and <b>not</b> during the invisibility
        /// step: the oracle's one deliberate hole in an otherwise all-ranged kit.
        /// </summary>
        public static bool ShouldContactAttack(int aiState, int attState, float distanceSquared, float roll)
        {
            if (aiState != 3 || attState == InvisibilityAttackState)
            {
                return false;
            }

            return distanceSquared < ContactAttackRangePixels * ContactAttackRangePixels
                   && roll < ContactAttackChance;
        }

        // ── the facing (UnitBossAlicorn.as:503-531) ───────────────────────────────────────────────

        /// <summary>
        /// <c>aiNapr = celX &gt; X ? 1 : -1;</c> (<c>:517</c>) — the side the boss <b>wants</b> to face,
        /// decided from an <b>absolute</b> aim point and her own live feet.
        /// </summary>
        /// <remarks>
        /// <para><b>Both arguments are absolute X coordinates, and that is the whole point.</b> The
        /// oracle compares <c>celX</c> — a field holding a world point — against <c>X</c>, the body's
        /// current position. Neither is a delta, so nothing goes stale when the body moves without the
        /// aim point moving. A stored <i>delta</i> (<c>celX - X</c>) cannot be compared this way after a
        /// teleport: it was measured against where she used to stand, and its sign now describes a
        /// direction she is no longer in.</para>
        ///
        /// <para><b>The strict <c>&gt;</c> is load-bearing.</b> <c>setPos</c> ends with
        /// <c>this.setCel()</c> (<c>Unit.as:1879</c>), and the no-argument overload assigns
        /// <c>celX = X</c> (<c>:4727-4731</c>) — so on the tick after every teleport the comparison is
        /// <c>X &gt; X</c>, which is <b>false</b>, which means <b>−1</b>. That is why the oracle's boss
        /// briefly faces left after landing anywhere, until <c>setCel(loc.gg)</c> refreshes the point on
        /// the next <c>aiTCh % 10 == 1</c> tick. Writing <c>&gt;=</c> here would silently delete that
        /// behaviour and make "no target" mean "keep the current facing".</para>
        /// </remarks>
        public static int TurnDirection(float celX, float selfX)
        {
            return celX > selfX ? 1 : -1;
        }

        /// <summary>
        /// The <c>t_turn</c> ladder (<c>:518-530</c>): a flip is not a snap, it is a countdown, and the
        /// countdown restarts every tick the facing is already right.
        /// </summary>
        /// <remarks>
        /// <c>if(storona != aiNapr) { --t_turn; if(t_turn &lt;= 0) { storona = aiNapr; t_turn = 15; } }
        /// else t_turn = 15;</c> — so the flip lands on the <b>15th</b> consecutive tick of disagreement,
        /// and any tick of agreement puts the budget back to 15. Half a second at 30 Hz, which is what
        /// makes the turn read as a turn.
        /// </remarks>
        /// <returns>Whether the facing should be applied this tick.</returns>
        public static bool TickTurn(int storona, int aiNapr, ref int tTurn)
        {
            if (storona == aiNapr)
            {
                tTurn = TurnTicks;
                return false;
            }

            --tTurn;
            if (tTurn > 0)
            {
                return false;
            }

            tTurn = TurnTicks;
            return true;
        }

        // ── flight ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>if(!stay &amp;&amp; !isFly) { --t_fly; if(t_fly &lt;= 0) isFly = true; } else t_fly = 3;</c>
        /// (<c>:378-389</c>).
        /// </summary>
        /// <remarks>
        /// <para><b><c>!stay</c> is "airborne", not "on the ground" — the double negative is the trap.</b>
        /// AS3's <c>stay</c> is true when the body is standing on something, so the countdown runs while
        /// the boss is <i>in the air</i> and not yet flying: it is a fall-to-hover timer, not a
        /// land-then-lift one. Reading it the other way makes a grounded boss lift off after three
        /// ticks, which looks like a working feature and is the opposite of the oracle's.</para>
        /// </remarks>
        /// <param name="tFly">AS3 <c>t_fly</c>.</param>
        /// <param name="onGround">AS3 <c>stay</c> — the body is standing on something.</param>
        /// <param name="isFlying">AS3 <c>isFly</c>.</param>
        /// <param name="nowFlying">The resulting <c>isFly</c>.</param>
        /// <returns>The new <c>t_fly</c>.</returns>
        public static int TickFlightTimer(int tFly, bool onGround, bool isFlying, out bool nowFlying)
        {
            if (!onGround && !isFlying)
            {
                int next = tFly - 1;
                nowFlying = next <= 0;
                return next;
            }

            nowFlying = isFlying;
            return FlyTicks;
        }

        /// <summary>
        /// <c>tile = loc.getAbsTile(X, Y + 10); if(tile.phis || tile.shelf) isFly = false; else
        /// isFly = true;</c> (<c>:323-331</c>) — the teleport lands the boss <b>on</b> a surface if the
        /// tile just below its feet is solid or a shelf, and in the air otherwise.
        ///
        /// <para><b>This is what makes the seven points a mix of perch and hover.</b> The two upper
        /// points (y=5, y=7) sit over the room's open span, so the boss arrives flying; the lower ones
        /// over the dais and the side ledges, where it arrives standing. A port that always flew would
        /// never use the <c>stay</c> row at all.</para>
        /// </summary>
        public static bool IsFlyingAfterTeleport(bool solidOrShelfBelow) => !solidOrShelfBelow;

        /// <summary>
        /// <c>if(isFly) { t_float += 0.0726; floatX = sin * 2; floatY = cos * 2; dx *= 0.9; dy *= 0.9;
        /// dx += floatX; dy += floatY; }</c> (<c>:489-498</c>) — the boss's only locomotion.
        /// </summary>
        /// <remarks>
        /// <para><b>The Y is mirrored, and this is the one sign in this class that had to be
        /// carried.</b> AS3's <c>dy += cos(t) * 2</c> adds a <i>downward</i> component at
        /// <c>t = 0</c> because its Y grows down. The port's velocity is y-up, so the equivalent is a
        /// <i>subtraction</i>. Getting it wrong is invisible in a screenshot — the bob is symmetric —
        /// but it puts the hover a half-period out of phase with the oracle, which is exactly the kind
        /// of error a fixture can catch and a play-test cannot.</para>
        /// </remarks>
        /// <param name="tFloat">The phase, advanced in place.</param>
        /// <param name="velocity">The body's current velocity in <b>px per frame, y-up</b>.</param>
        /// <returns>The velocity the bob wants, in the same space.</returns>
        public static void FloatBob(ref float tFloat, ref float velocityX, ref float velocityY)
        {
            tFloat += FloatPhaseStep;

            float floatX = (float)Math.Sin(tFloat) * FloatAmplitude;
            float floatY = (float)Math.Cos(tFloat) * FloatAmplitude;

            velocityX = velocityX * FloatDamping + floatX;

            // `dy += floatY` in AS3's y-down space is `-=` in the port's y-up space.
            velocityY = velocityY * FloatDamping - floatY;
        }

        // ── the shield break (UnitBossAlicorn.as:703-738) ─────────────────────────────────────────

        /// <summary>
        /// <c>if(isShit) { isShit = false; … hp = maxhp - 0.1; … }</c> (<c>:709-713</c>).
        ///
        /// <para><b>Why <c>- 0.1</c> and not a fraction.</b> The dome is not a pool: the boss takes
        /// damage normally for its whole first stage, and the <i>first lethal hit</i> shatters the dome
        /// and leaves it at "full, minus a tenth of a point" — alive, and with its HP bar reading full.
        /// That is the oracle's own arithmetic and it is what makes the second stage look like a
        /// separate health bar.</para>
        /// </summary>
        public static float HpAfterShieldBreak(float maxHp) => maxHp - 0.1f;

        /// <summary>
        /// <c>setNull(): if(!isNoResBoss()) isShit = true;</c> (<c>:202-207</c>) — a respawned boss
        /// gets its dome back. The port has no resurrect, but a room that is re-entered rebuilds the
        /// unit, so this is the value a fresh brain must start from.
        /// </summary>
        public const bool ShieldOnRespawn = true;

        // ── difficulty scaling (UnitBossAlicorn.as:141-164) ───────────────────────────────────────

        /// <summary>
        /// <c>_loc3_ = 1; if(globalDif == 3) _loc3_ = 1.2; if(globalDif == 4) _loc3_ = 1.5;</c>
        /// (<c>:145-153</c>) — the boss's HP <b>and</b> its current weapon's damage.
        /// </summary>
        public static float DifficultyScale(int difficulty)
        {
            if (difficulty >= 4) return 1.5f;
            if (difficulty == 3) return 1.2f;
            return 1f;
        }

        /// <summary>
        /// <c>_loc2_ = 1 + level * 0.07</c> (<c>:144</c>) — applied to <c>weaps[1]</c>
        /// (<c>aliblade</c>, the melee arm) and to nothing else.
        /// </summary>
        /// <remarks>
        /// <b>Faithful oddity, kept.</b> Only the second weapon scales with level; the other three do
        /// not. The port has no NPC level at all (the same gap <c>UnitController.SkinResistance</c>
        /// records), so callers pass <c>0</c> and this returns <c>1</c> — but the asymmetry is modelled
        /// rather than flattened, because a later level system would otherwise silently buff all four.
        /// </remarks>
        public static float BladeLevelScale(float level) => 1f + level * 0.07f;

        // ── telekinesis gates (UnitBossAlicorn.as:591-619) ────────────────────────────────────────

        /// <summary><c>if(celUnit &amp;&amp; isrnd(0.5))</c> (<c>:594</c>).</summary>
        public static bool PrefersUnitTarget(float roll) => roll < UnitGrabChance;

        /// <summary><c>isrnd(0.3)</c> per candidate prop (<c>:606</c>).</summary>
        public static bool AcceptsProp(float roll) => roll < PropGrabChance;

        /// <summary><c>getRasst2(obj) &lt;= optDistTele * optDistTele</c> (<c>:608</c>).</summary>
        public static bool InTeleRange(float distanceSquared)
        {
            return distanceSquared <= TeleRangePixels * TeleRangePixels;
        }

        /// <summary>
        /// <c>_loc2_ = throwForce; if(massa &gt; 1) _loc2_ /= sqrt(massa); if(X &lt; 200 || X &gt; limX-200)
        /// _loc2_ *= 0.6;</c> (<c>:663-671</c>).
        /// </summary>
        /// <param name="nearWall">
        /// The held object is within <see cref="NearWallMarginPixels"/> of either vertical room edge.
        /// </param>
        public static float ThrowSpeed(float throwForce, float massa, bool nearWall)
        {
            float speed = AlicornVariantRules.ThrowSpeed(throwForce, massa);
            return nearWall ? speed * NearWallThrowFactor : speed;
        }

        /// <summary>
        /// Whether a held object is close enough to a vertical room edge for
        /// <see cref="ThrowSpeed"/>'s damping — <c>X &lt; 200 || X &gt; limX - 200</c> (<c>:668</c>),
        /// in room-local pixels.
        /// </summary>
        public static bool IsNearWall(float objectX, float roomLimitX)
        {
            return objectX < NearWallMarginPixels || objectX > roomLimitX - NearWallMarginPixels;
        }
    }
}
