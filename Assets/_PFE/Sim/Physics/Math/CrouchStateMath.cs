using System;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// AS3's <c>isSit</c> — the player's crouch — as the state machine the oracle actually implements:
    /// a <b>latch</b>, not a key level.
    ///
    /// <para><b>The defect this exists to fix.</b> The port computed
    /// <c>_isCrouching = grounded &amp;&amp; !water &amp;&amp; !ladder &amp;&amp; move.y &lt; -0.5</c> every fixed step
    /// (<c>PlayerLocomotionController</c>), i.e. it treated crouch as "the Down key is held right
    /// now". The oracle does not: <c>UnitPlayer.as:2859</c> calls <c>sit(true)</c> once, and the flag
    /// then <i>persists</i> — releasing Down leaves the player crouched and crawling at
    /// <c>sitSpeed</c>. It is cleared in exactly two places, <c>:2884</c>
    /// (<c>isSit &amp;&amp; keyBeUp &amp;&amp; !keySit</c> — press <b>Up</b> while not holding Down) and
    /// <c>:2889</c> (<c>isSit &amp;&amp; !stay</c> — leave the ground). A level key and a latch are not
    /// the same behaviour in either direction: the level version stands the player up the instant Down
    /// is released, so the whole crouch-crawl (and the roll) is unreachable, and it also cannot express
    /// the oracle's "you keep crouching under a low ceiling because <c>unsit()</c> re-sits you".</para>
    ///
    /// <para><b>The hold debounce is part of the mechanic, not smoothing.</b> AS3 counts
    /// <c>downp</c> (<c>:2878-2881</c>, saturating at 6) while Down is held and tests it:
    /// <c>stayPhis == 1 &amp;&amp; downp == 1 || stayPhis == 2 &amp;&amp; downp == 5</c> (<c>:2859</c>).
    /// <c>stayPhis</c> is what the unit is standing on — <c>1</c> solid ground, <c>2</c> a one-way
    /// <c>shelf</c> (the port's <c>MovementMotorState.IsOnPlatform</c>). So a <b>tap</b> of Down never
    /// crouches: over solid ground it takes a second frame, and over a shelf it takes six. That delay
    /// is what keeps the <b>double-tap drop-through</b> (<c>keyDubSit</c>, <c>:2917-2925</c>) from
    /// turning into a crouch on the way down — the two gestures start with the same key.</para>
    ///
    /// <para><b>The roll.</b> <c>anim()</c> picks <c>"roll"</c> while sitting when
    /// <c>maxSpeed &gt; walkSpeed * 1.6 &amp;&amp; dx * storona &gt; 0 &amp;&amp; (runForever || keyRun
    /// &amp;&amp; (keyLeft || keyRight))</c> (<c>:4424</c>). While sitting, <c>maxSpeed</c> is
    /// <c>sitSpeed</c> (<c>:2476-2478</c>), which is half of <c>walkSpeed</c> — so that test can only
    /// ever be true on the <b>one frame the crouch begins</b>, because <c>maxSpeed</c> for that frame
    /// was computed <i>before</i> <c>sit(true)</c> ran (<c>:2457-2490</c> vs <c>:2859</c>). That is
    /// exactly the reported behaviour: crouch <i>out of a sprint</i> and the character rolls; crouch
    /// from a standstill or a walk and it goes straight to the crawl. Then
    /// <c>:4417-4420</c> hands the animation over to <c>"polz"</c> once the roll clip reaches frame 15.
    /// The roll is therefore a <b>transition, not a state</b> — it changes nothing about the collision
    /// box, the speed or the controls.</para>
    ///
    /// <para><b>Deliberately not modelled.</b> <c>porog = 0</c> while Down is held (the step-up
    /// allowance, <c>:2837</c>) belongs to the motor's step logic; <c>diagon == 0</c> is vacuous here
    /// because the port has no diagonal locomotion at all (audit row 22); <c>isFetter</c>,
    /// <c>noStairs</c>, <c>cryst</c> and <c>rat</c> are states this port does not have. Each is named
    /// so a future reader can tell "absent" from "forgotten".</para>
    /// </summary>
    public static class CrouchStateMath
    {
        /// <summary>
        /// The vertical deadzone that turns an analogue axis into "Down". The same <c>-0.5</c>
        /// <c>UnitDropThroughMath.DownThreshold</c> names — kept equal here because the crouch and the
        /// drop-through are driven by the same key and must not disagree about where it starts.
        /// </summary>
        public const float DownThreshold = -0.5f;

        /// <summary>
        /// AS3 <c>ctr.keyBeUp</c> as an axis — the key that <i>ends</i> the crouch
        /// (<c>UnitPlayer.as:2884</c>). Also the same magnitude as <see cref="DownThreshold"/>, so a
        /// stick at rest is neither.
        /// </summary>
        public const float UpThreshold = 0.5f;

        /// <summary>
        /// AS3 <c>downp == 1</c> over solid ground (<c>stayPhis == 1</c>, <c>UnitPlayer.as:2859</c>).
        /// <c>downp</c> is incremented at the <i>end</i> of the frame that tested it (<c>:2878-2881</c>),
        /// so the test passes on the <b>second</b> frame Down is held — hence 2, not 1.
        /// </summary>
        public const int SolidGroundHoldFrames = 2;

        /// <summary>
        /// AS3 <c>stayPhis == 2 &amp;&amp; downp == 5</c> — the same test over a one-way shelf, i.e. on
        /// the <b>sixth</b> held frame. The extra four frames are the window in which a double-tap must
        /// be recognisable as a drop-through instead of a crouch.
        /// </summary>
        public const int ShelfHoldFrames = 6;

        /// <summary>
        /// AS3 <c>maxSpeed &gt; walkSpeed * 1.6</c> (<c>UnitPlayer.as:4424</c>) — the speed gate on the
        /// roll. With the oracle's own constants (<c>runSpeed = walkSpeed * 2</c>, <c>:356</c>) a run
        /// clears it and a walk (1.0) does not.
        /// </summary>
        public const float RollSpeedRatio = 1.6f;

        /// <summary>
        /// AS3 <c>vis.osn.body.currentFrame &gt;= 15</c> (<c>UnitPlayer.as:4417</c>) — the frame of the
        /// <c>roll</c> clip at which the animation hands over to <c>polz</c>. Read by the animation
        /// state, not by the movement tick; it lives here so the clip's one magic number has one home.
        /// </summary>
        public const int RollHandoffFrame = 15;

        /// <summary>AS3 <c>ctr.keySit</c> (<c>Ctr.as:17</c>, <c>Keyboard.S</c>) as an axis level.</summary>
        public static bool SitHeld(float moveY)
        {
            return moveY < DownThreshold;
        }

        /// <summary>AS3 <c>ctr.keyBeUp</c> (<c>Ctr.as:15</c>, <c>Keyboard.W</c>) as an axis level.</summary>
        public static bool UpHeld(float moveY)
        {
            return moveY > UpThreshold;
        }

        /// <summary>
        /// How long Down must be held before the crouch latches — AS3 <c>downp</c>'s threshold, in
        /// frames, selected by what the unit is standing on.
        /// </summary>
        /// <param name="onShelf">AS3 <c>stayPhis == 2</c> — the port's <c>IsOnPlatform</c>.</param>
        public static int RequiredHoldFrames(bool onShelf)
        {
            return onShelf ? ShelfHoldFrames : SolidGroundHoldFrames;
        }

        /// <summary>
        /// <see cref="RequiredHoldFrames"/> converted to seconds at the <b>canonical</b> AS3 frame rate.
        ///
        /// <para><b>Why seconds and not a tick counter.</b> <c>downp</c> counts AS3 frames, and an AS3
        /// frame is 1/30 s. The port's <c>FixedUpdate</c> runs at the project's fixed timestep, which is
        /// not 30 Hz, so counting <i>fixed steps</i> would make a six-frame debounce last 120 ms on a
        /// 50 Hz build and 100 ms on 60 Hz. Dividing by <c>SimClock.FramesPerSecond</c> keeps the
        /// duration the oracle's on every build, which is the same conversion
        /// <c>MineObject</c>/<c>ThrownObject</c> use for their fuse timers.</para>
        ///
        /// <para><b>The parameter must be the canonical rate, never the live tick rate.</b> Passing
        /// <c>SimClock.TicksPerSecond</c> would shorten the debounce on a 60 Hz build, which is exactly
        /// the "nothing may derive from a different value" mistake
        /// <c>PlayerLocomotionController.PublishPrecisionState</c> documents for the precision
        /// thresholds. It is a parameter rather than a read of <c>SimClock</c> so this file stays
        /// Unity- and assembly-free and can be executed in the offline harness.</para>
        /// </summary>
        /// <param name="onShelf">AS3 <c>stayPhis == 2</c> — the port's <c>IsOnPlatform</c>.</param>
        /// <param name="canonicalFramesPerSecond"><c>SimClock.FramesPerSecond</c> — 30, not the tick rate.</param>
        public static float RequiredHoldSeconds(bool onShelf, float canonicalFramesPerSecond)
        {
            if (canonicalFramesPerSecond <= 0f)
            {
                return 0f;
            }

            return RequiredHoldFrames(onShelf) / canonicalFramesPerSecond;
        }

        /// <summary>
        /// AS3 <c>UnitPlayer.as:2839</c> — may the crouch latch <i>at all</i> this frame?
        /// <c>stay &amp;&amp; diagon == 0 &amp;&amp; runForever &lt;= 0 &amp;&amp; !inWater &amp;&amp;
        /// isFetter &lt;= 0 &amp;&amp; !noStairs &amp;&amp; rat == 0</c>, reduced to the terms this port
        /// has.
        ///
        /// <para><b>Why <c>!onLadder</c> is in here rather than in the caller.</b> In the oracle a
        /// ladder is the <c>isLaz</c> arm of the same <c>if(keySit)</c> block (<c>:2862-2867</c>): Down
        /// <i>descends</i> instead of crouching, so the two are alternatives at one branch point and
        /// cannot both be taken. Splitting them across two call sites is how a player ends up crouched
        /// on a ladder.</para>
        /// </summary>
        /// <param name="grounded">AS3 <c>stay</c>.</param>
        /// <param name="inWater">AS3 <c>inWater</c> — Down swims down instead.</param>
        /// <param name="onLadder">AS3 <c>isLaz</c> — Down descends instead.</param>
        /// <param name="runForced">AS3 <c>runForever &gt; 0</c> — the horror effect refuses the crouch.</param>
        public static bool MayBeginCrouch(bool grounded, bool inWater, bool onLadder, bool runForced)
        {
            return grounded && !inWater && !onLadder && !runForced;
        }

        /// <summary>
        /// AS3's two release conditions, <c>UnitPlayer.as:2884</c>
        /// (<c>isSit &amp;&amp; keyBeUp &amp;&amp; !keySit</c>) and <c>:2889</c> (<c>isSit &amp;&amp; !stay</c>).
        ///
        /// <para><b>Not modelled here: the headroom test.</b> AS3's <c>unsit()</c> calls
        /// <c>sit(false)</c> and then re-sits if <c>collisionAll()</c> reports a collision
        /// (<c>Unit.as:2532-2538</c>). The port's <c>TilePhysicsController.UpdateColliderProfile</c>
        /// already does exactly that — it refuses to grow the box back while
        /// <c>HasSolidOverlapAt(standing height)</c> — so the latch may be released while the
        /// <i>collision box</i> stays low, and the two must not both try to own that decision.</para>
        /// </summary>
        /// <param name="upHeld">AS3 <c>ctr.keyBeUp</c>.</param>
        /// <param name="sitHeld">AS3 <c>ctr.keySit</c> — holding both does not stand you up.</param>
        /// <param name="standing">AS3 <c>stay</c> (the port's grounded-and-not-on-a-ladder).</param>
        public static bool ShouldEndCrouch(bool upHeld, bool sitHeld, bool standing)
        {
            if (!standing)
            {
                return true;
            }

            return upHeld && !sitHeld;
        }

        /// <summary>
        /// AS3 <c>UnitPlayer.as:4424</c> — does this crouch begin as a <b>roll</b> rather than a crawl?
        ///
        /// <para>Every term is the oracle's, and each one is load-bearing:</para>
        /// <list type="bullet">
        /// <item><paramref name="speedBeforeCrouch"/> — the <c>maxSpeed</c> this frame would have had
        /// with the crouch <i>off</i>. It is the whole reason the roll is an entry-only transition:
        /// a frame later that number is <c>sitSpeed</c>.</item>
        /// <item><paramref name="movingForward"/> — <c>dx * storona &gt; 0</c>. Backpedalling into a
        /// crouch must not roll; the oracle's animation branches are all facing-gated (audit §3c).</item>
        /// <item><paramref name="runHeld"/>/<paramref name="horizontalInput"/> — the oracle's
        /// <c>keyRun &amp;&amp; (keyLeft || keyRight)</c>: the run key alone, with no direction, does not
        /// roll. <paramref name="runForced"/> substitutes for the whole pair, exactly as
        /// <c>runForever</c> does.</item>
        /// </list>
        /// </summary>
        /// <param name="speedBeforeCrouch">Pixels/frame; the run or walk speed, never the sit speed.</param>
        /// <param name="walkSpeed">Pixels/frame — the divisor of <see cref="RollSpeedRatio"/>.</param>
        /// <param name="horizontalVelocity">Pixels/frame; the sign is what the facing test reads.</param>
        /// <param name="facingDirection">+1 or -1 — AS3 <c>storona</c>.</param>
        /// <param name="horizontalInput">The raw move axis, −1..1.</param>
        public static bool ShouldRoll(
            float speedBeforeCrouch,
            float walkSpeed,
            float horizontalVelocity,
            int facingDirection,
            float horizontalInput,
            bool runHeld,
            bool runForced)
        {
            if (walkSpeed <= 0f)
            {
                return false;
            }

            bool fastEnough = speedBeforeCrouch > walkSpeed * RollSpeedRatio;
            bool movingForward = horizontalVelocity * facingDirection > 0f;
            bool runGesture = runForced || (runHeld && Math.Abs(horizontalInput) > 0.1f);

            return fastEnough && movingForward && runGesture;
        }
    }
}
