using System;
using PFE.Data.Definitions;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// The Enclave Commander's fight as pure arithmetic — AS3 <c>UnitBossEncl</c>
    /// (<c>UnitBossEncl.as</c>): the flight box and sine drift at <c>control():212-299</c>, the
    /// per-<c>tr</c> loadout at <c>:50-73</c>, the shared <c>Coord</c> turn-taking at <c>:306</c>
    /// (<c>Coord.as:57-78</c>), the difficulty multiply at <c>setLevel():113-132</c> and the
    /// constructor's timer/drift literals at <c>:25-35</c>.
    ///
    /// <para><b>Why this is a separate, Unity-free class.</b> The state lives on a
    /// <c>MonoBehaviour</c> that <c>AddComponent</c> builds, and <c>AddComponent</c> reaches a Unity
    /// <c>ECall</c>, so a fixture cannot construct the brain on the offline host. Splitting the
    /// <i>rules</i> out means the numbers are pinned by an ordinary NUnit fixture and the brain is
    /// reduced to "read the unit, ask this, write the unit back" — the same split
    /// <see cref="BossRaiderRules"/>, <see cref="BossUltraRules"/> and
    /// <see cref="BossAlicornRules"/> already use.</para>
    ///
    /// <para><b>There are no phases in this fight, and that is the oracle's shape.</b> The boss is
    /// <b>three co-spawned commanders</b> (<c>tr</c> 1/2/3, <c>RoomsProb.as:896-898</c>) that share one
    /// <c>Coord</c>; the "phase" is the rotating firing right, and it lives in
    /// <see cref="BossEnclCoord"/>, not on any one commander. A port that invented HP thresholds here
    /// would be adding a mechanic the oracle never had — the exact defect
    /// <c>90_Boss_Implementation.md</c> §13.1 records for the alicorn.</para>
    /// </summary>
    public static class BossEnclRules
    {
        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  Identity and the per-tr loadout (UnitBossEncl.as:39-94)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>tr</c>'s default — <c>public var tr:int = 1</c> (<c>:11</c>).</summary>
        public const int DefaultCommander = 1;

        /// <summary><c>coord.tr</c> cycles over exactly three commanders (<c>Coord.as:16,65-77</c>).</summary>
        public const int CommanderCount = 3;

        /// <summary><c>Weapon.create(this,"lmg")</c> for <c>tr == 1</c> (<c>:52</c>).</summary>
        public const string LmgWeaponId = "lmg";

        /// <summary><c>Weapon.create(this,"quick")</c> for <c>tr == 2</c> (<c>:58</c>).</summary>
        public const string QuickWeaponId = "quick";

        /// <summary><c>Weapon.create(this,"mlau")</c> for <c>tr == 3</c> (<c>:65</c>).</summary>
        public const string MlauWeaponId = "mlau";

        /// <summary>
        /// The weapon a <c>tr</c> selects — <c>if(this.tr == 1) … "lmg"; if(this.tr == 2) … "quick";
        /// if(this.tr == 3) … "mlau"</c> (<c>:50-73</c>).
        ///
        /// <para><b>Three separate <c>if</c>s, not a switch with a default.</b> A <c>tr</c> outside
        /// <c>1..3</c> leaves <c>currentWeapon</c> <c>null</c> in the oracle — the commander exists,
        /// flies, and never fires, because <c>coord.tr</c> only ever equals 1, 2 or 3. Returning
        /// <c>null</c> here is that state, not an error.</para>
        /// </summary>
        public static string WeaponIdFor(int tr)
        {
            if (tr == 1) return LmgWeaponId;
            if (tr == 2) return QuickWeaponId;
            if (tr == 3) return MlauWeaponId;
            return null;
        }

        /// <summary>
        /// <c>tr == 1</c>: <c>armor = 20</c> (<c>:53</c>). <c>tr == 2</c>: <c>marmor = 20</c>
        /// (<c>:59</c>). <c>tr == 3</c>: <c>armor = marmor = 10</c> (<c>:70</c>). Everything else: none.
        ///
        /// <para><b>This is a class-side override, not the unit row.</b> <c>bossencl.asset</c> carries
        /// <c>armor: 0</c>/<c>magicArmor: 0</c> because <c>AllData.as:637-647</c> authors no
        /// <c>@armor</c>/<c>@marmor</c>; the ratings exist only in the constructor, which is why the
        /// port read 0/0 for every commander.</para>
        /// </summary>
        public static void ArmourFor(int tr, out int physical, out int magic)
        {
            physical = 0;
            magic = 0;

            if (tr == 1) physical = 20;
            else if (tr == 2) magic = 20;
            else if (tr == 3) physical = magic = 10;
        }

        /// <summary>
        /// The constructor's <c>vulner[...] = 0.7</c> triples (<c>:54</c>, <c>:60</c>, <c>:71</c>),
        /// applied on top of the row's authored <c>&lt;vulner&gt;</c> table.
        ///
        /// <para><b>Which three, per <c>tr</c> — and the sets do not overlap:</b> tr1 bullet/physical/
        /// blade, tr2 laser/plasma/spark, tr3 explosive/fire/cryo. The row already authors
        /// <c>laser 0.7</c>, <c>plasma 0.85</c> and <c>venom 0.5</c> (<c>AllData.as:641</c>); tr2's
        /// assignment therefore <i>raises</i> plasma from 0.85 to 0.7, exactly as the oracle does by
        /// writing the same slot again after the XML read.</para>
        /// </summary>
        public static VulnerabilityData ApplyVulnerabilities(VulnerabilityData table, int tr)
        {
            if (tr == 1)
            {
                table.bullet = 0.7f;
                table.phis = 0.7f;
                table.blade = 0.7f;
            }
            else if (tr == 2)
            {
                table.laser = 0.7f;
                table.plasma = 0.7f;
                table.spark = 0.7f;
            }
            else if (tr == 3)
            {
                table.expl = 0.7f;
                table.fire = 0.7f;
                table.cryo = 0.7f;
            }

            return table;
        }

        // ── tr3's post-construction weapon tuning (UnitBossEncl.as:66-69) ────────────────────────
        //
        // `currentWeapon.speed = 12; currentWeapon.accel = 0.6; currentWeapon.reload = 30;
        //  currentWeapon.damageExpl *= 0.6;`
        //
        // The oracle mutates the INSTANCE, so the port must clone the shared `mlau` definition before
        // stamping these — writing the asset would slow every other launcher in the game.

        /// <summary><c>currentWeapon.speed = 12</c> (<c>:66</c>) — the rocket's px/frame speed.</summary>
        public const float Tr3ProjectileSpeed = 12f;

        /// <summary><c>currentWeapon.accel = 0.6</c> (<c>:67</c>).</summary>
        public const float Tr3BulletAcceleration = 0.6f;

        /// <summary><c>currentWeapon.reload = 30</c> (<c>:68</c>) — reload <b>duration</b>, in frames.</summary>
        public const int Tr3ReloadFrames = 30;

        /// <summary><c>currentWeapon.damageExpl *= 0.6</c> (<c>:69</c>) — 250 becomes 150.</summary>
        public const float Tr3ExplosiveDamageScale = 0.6f;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  The flight box and drift (UnitBossEncl.as:25-35, 258-294)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>minX = 1000</c> (<c>:29</c>) — room-local pixels, AS3's x axis.</summary>
        public const float ArenaMinX = 1000f;

        /// <summary><c>maxX = 1600</c> (<c>:31</c>).</summary>
        public const float ArenaMaxX = 1600f;

        /// <summary><c>minY = 250</c> (<c>:25</c>) — AS3's y, measured <b>down</b> from the ceiling.</summary>
        public const float ArenaMinYAs3 = 250f;

        /// <summary><c>maxY = 850</c> (<c>:27</c>) — AS3's y, measured down from the ceiling.</summary>
        public const float ArenaMaxYAs3 = 850f;

        /// <summary>
        /// AS3 room-local pixel Y (down from the ceiling) to the port's room-local pixel Y (up from
        /// the floor). The mirror is the room height, exactly as <c>WorldCoordinates</c> does it.
        /// </summary>
        public static float PortYFromAs3(float as3Y, float roomHeightPixels) =>
            roomHeightPixels - as3Y;

        /// <summary><c>sinX = Math.random() * 10</c> (<c>:33</c>) — the phase, drawn once per commander.</summary>
        public const float SinPhaseRange = 10f;

        /// <summary><c>sinDX = Math.random() * 0.1 + 0.02</c> (<c>:35</c>) — 0.02..0.12 per frame.</summary>
        public const float SinDeltaBase = 0.02f;

        /// <summary>The <c>0.1</c> in the same line — <c>random() * 0.1</c>, so the ceiling is 0.12 exclusive.</summary>
        public const float SinDeltaRange = 0.1f;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  control()'s own timers (UnitBossEncl.as:48, 236-255)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>aiTCh = 30</c> in the constructor (<c>:48</c>) — the spawn hover before <c>aiState</c> becomes 1.</summary>
        public const int AiTicksInitial = 30;

        /// <summary><c>aiVNapr = 1</c> in the constructor (<c>:49</c>) — the first vertical push is downward.</summary>
        public const int AiVerticalDirectionInitial = 1;

        /// <summary><c>aiTCh = 3</c> when stunned (<c>:221</c>) — the pause before the flight resumes.</summary>
        public const int StunTicks = 3;

        /// <summary><c>Math.floor(Math.random() * 60 + 150)</c> (<c>:243</c>) — the window base.</summary>
        public const int AiTicksBase = 150;

        /// <summary>The <c>60</c> in the same line — the roll is 0..59, so the window is 150..209.</summary>
        public const int AiTicksRollRange = 60;

        /// <summary><c>if(aiTCh % 40 == 1)</c> (<c>:245</c>) — the <c>setCel</c> poll cadence.</summary>
        public const int CelCadence = 40;

        /// <summary>The <c>1</c> in the same modulus — the phase of the poll within the window.</summary>
        public const int CelOffset = 1;

        /// <summary>
        /// <c>aiTCh % 40 == 1</c>, as a predicate. The value is the countdown <i>after</i> the
        /// decrement, which is why the phase is 1 and not 0.
        /// </summary>
        public static bool IsCelPollTick(int aiTicks) => aiTicks % CelCadence == CelOffset;

        /// <summary>The next window length for a draw of <paramref name="roll"/> in 0..59.</summary>
        public static int AiTicksFor(int roll) => AiTicksBase + roll;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  The flight model (UnitBossEncl.as:258-294)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One commander's flight state, in the oracle's own units: room-local pixels with <b>Y growing
        /// downward from the ceiling</b>, and velocity in pixels per 30 Hz frame with <c>Dy</c> positive
        /// meaning <i>down</i>.
        ///
        /// <para><b>Why AS3's orientation is kept instead of mirrored on entry.</b> The arena box is
        /// authored as <c>minY 250</c> / <c>maxY 850</c> measured down from the ceiling, and every
        /// comparison in <c>control()</c> reads that axis. Converting the box to the port's up-is-positive
        /// Y would flip four inequalities and both <c>aiVNapr</c> arms, and each flip is a chance to
        /// invert one — the trap the project's own notes name for <c>AS3 Y down, port up</c>. Keeping the
        /// oracle's axis here means the rules are a transcription, and the single mirror happens at the
        /// seam that talks to the motor (<see cref="PortVerticalSpeedFromAs3"/>).</para>
        /// </summary>
        public struct BossEnclFlight
        {
            /// <summary><c>X</c> in AS3 room-local pixels.</summary>
            public float X;

            /// <summary><c>Y</c> in AS3 room-local pixels, measured <b>down</b> from the ceiling.</summary>
            public float Y;

            /// <summary><c>dx</c>, px/frame. Positive is right.</summary>
            public float Dx;

            /// <summary><c>dy</c>, px/frame. <b>Positive is down.</b></summary>
            public float Dy;

            /// <summary><c>sinX</c> — the drift phase (<c>:33</c>).</summary>
            public float SinX;

            /// <summary><c>sinDX</c> — the phase advance per frame, 0.02..0.12 (<c>:35</c>).</summary>
            public float SinDelta;

            /// <summary>
            /// <c>aiVNapr</c> — <c>1</c> while the boss is being pushed <i>down</i>, <c>-1</c> while it
            /// is pushed <i>up</i>. It is a latch, not a reading of <c>dy</c>: outside the box the arm
            /// that fires sets it, and inside the box it decides which way the push continues.
            /// </summary>
            public int VerticalDirection;
        }

        /// <summary>
        /// <c>control()</c>'s flight block (<c>:258-294</c>), for one frame.
        ///
        /// <code>
        /// if(aiState != 0) {
        ///    sinX += sinDX;
        ///    if(Y &lt; minY &amp;&amp; dy &lt; maxSpeed)          { dy += accel; aiVNapr = 1; }
        ///    if(Y &gt; maxY &amp;&amp; dy &gt; -maxSpeed)         { dy -= accel; aiVNapr = -1; }
        ///    if(Y &gt;= minY &amp;&amp; Y &lt;= maxY) {
        ///       if(aiVNapr == 1  &amp;&amp; dy &lt; maxSpeed)   dy += accel;
        ///       if(aiVNapr == -1 &amp;&amp; dy &gt; -maxSpeed)  dy -= accel;
        ///    }
        ///    if(X &lt; minX &amp;&amp; dx &lt; maxSpeed)            dx += accel;
        ///    if(X &gt; maxX &amp;&amp; dx &gt; -maxSpeed)           dx -= accel;
        ///    if(X &gt;= minX &amp;&amp; X &lt;= maxX)               dx += Math.sin(sinX) * accel / 2;
        /// }
        /// </code>
        ///
        /// <para><b>Four separate <c>if</c>s per axis, and the box arms are not exclusive.</b> A unit
        /// above the box takes the first arm and <i>not</i> the in-box arm (its <c>Y &gt;= minY</c> test
        /// fails), which is what makes the boss turn around at the wall instead of accelerating through
        /// it. Collapsing them into an if/else-if chain reads the same on the happy path and changes the
        /// velocity one frame early at every crossing.</para>
        ///
        /// <para><b><c>maxSpeed</c> caps each axis independently.</b> The oracle never damps: once
        /// <c>dx</c> reaches <c>maxSpeed</c> it stays there until the far wall's arm subtracts from it.
        /// That is why the commander crosses its box rather than hovering — and why the port must not
        /// "helpfully" add drag.</para>
        ///
        /// <para><b><paramref name="active"/> is <c>aiState != 0</c>, and it gates the phase too.</b>
        /// A stunned commander (<c>aiState = 0</c>, <c>:220</c>) freezes mid-drift and resumes from the
        /// same <c>sinX</c>, which is observable: a stun during the outbound leg does not change where
        /// the leg ends.</para>
        /// </summary>
        /// <param name="maxSpeed">AS3 <c>maxSpeed</c> — the unit row's <c>speed</c> (15 for <c>bossencl</c>).</param>
        /// <param name="accel">AS3 <c>accel</c> — the unit row's <c>accel</c> (1.5).</param>
        public static void StepFlight(ref BossEnclFlight flight, bool active, float maxSpeed, float accel)
        {
            if (!active) return;

            flight.SinX += flight.SinDelta;

            // `if(Y < minY && dy < maxSpeed)` — above the box's top edge, push DOWN.
            if (flight.Y < ArenaMinYAs3 && flight.Dy < maxSpeed)
            {
                flight.Dy += accel;
                flight.VerticalDirection = 1;
            }

            // `if(Y > maxY && dy > -maxSpeed)` — below the box's bottom edge, push UP.
            if (flight.Y > ArenaMaxYAs3 && flight.Dy > -maxSpeed)
            {
                flight.Dy -= accel;
                flight.VerticalDirection = -1;
            }

            // Inside the box, keep pushing the way the latch last pointed.
            if (flight.Y >= ArenaMinYAs3 && flight.Y <= ArenaMaxYAs3)
            {
                if (flight.VerticalDirection == 1 && flight.Dy < maxSpeed)
                {
                    flight.Dy += accel;
                }

                if (flight.VerticalDirection == -1 && flight.Dy > -maxSpeed)
                {
                    flight.Dy -= accel;
                }
            }

            // Horizontal: bounce off the box, and drift sinusoidally while inside it.
            if (flight.X < ArenaMinX && flight.Dx < maxSpeed)
            {
                flight.Dx += accel;
            }

            if (flight.X > ArenaMaxX && flight.Dx > -maxSpeed)
            {
                flight.Dx -= accel;
            }

            if (flight.X >= ArenaMinX && flight.X <= ArenaMaxX)
            {
                flight.Dx += (float)Math.Sin(flight.SinX) * accel / 2f;
            }
        }

        /// <summary>
        /// AS3 <c>dy</c> (positive <b>down</b>) to the port's vertical speed (positive <b>up</b>).
        ///
        /// <para><b>The one mirror in this file, named so it can be pinned.</b> Every other quantity the
        /// flight model handles stays in AS3's orientation; this is the single place the axis flips, and
        /// it flips at the seam that writes to the motor. A port that forgot it would drive the
        /// commander into the ceiling on every "push down" frame — and the arithmetic above would still
        /// be perfectly correct, which is exactly why the sign gets its own function and its own
        /// fixture.</para>
        /// </summary>
        public static float PortVerticalSpeedFromAs3(float as3Dy) => -as3Dy;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  The shared Coord (Coord.as:30-39, 57-100)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>this.t1 = 100</c> in the constructor (<c>Coord.as:36</c>).</summary>
        public const int CoordT1Initial = 100;

        /// <summary><c>this.t1 = Math.floor(Math.random() * 60 + 150)</c> (<c>Coord.as:63</c>).</summary>
        public const int CoordT1Base = 150;

        /// <summary>The <c>60</c> in the same line — 0..59, so the window is 150..209.</summary>
        public const int CoordT1RollRange = 60;

        /// <summary>
        /// <c>coord.tr == this.tr &amp;&amp; coord.t1 &gt; 45</c> (<c>UnitBossEncl.as:306</c>) — the
        /// firing gate. <b>45, strictly.</b>
        ///
        /// <para>The consequence is the fight's rhythm: a window of 150..209 ticks gives the focused
        /// commander about 105..164 ticks of fire and then 45 ticks of silence before the right moves
        /// on. A port that fired for the whole window (or gated on <c>&gt;= 45</c>) changes the
        /// encounter's tempo without changing any single number a reviewer would check.</para>
        /// </summary>
        public const int CoordFireGate = 45;

        /// <summary><c>this.t2 = 150</c> in the constructor (<c>Coord.as:37</c>).</summary>
        public const int CoordT2Initial = 150;

        /// <summary><c>this.t2 = 300</c> after each door cycle (<c>Coord.as:97</c>).</summary>
        public const int CoordT2Cycle = 300;

        /// <summary><c>if(this.t2 == 75)</c> — the red/green warning flash (<c>Coord.as:80</c>).</summary>
        public const int CoordSignalTick = 75;

        /// <summary><c>kolAll = 6</c> (<c>Coord.as:24</c>) — the six tagged doors <c>a1..a6</c>.</summary>
        public const int DoorCount = 6;

        /// <summary><c>kolClosed = 3</c> (<c>Coord.as:26</c>) — three of the six are shut at a time.</summary>
        public const int ClosedDoorCount = 3;

        /// <summary><c>if(coord.tr == this.tr &amp;&amp; coord.t1 &gt; 45)</c> (<c>:306</c>).</summary>
        public static bool ShouldFire(int coordTr, int coordT1, int commanderTr) =>
            coordTr == commanderTr && coordT1 > CoordFireGate;

        /// <summary>
        /// <c>Coord.step()</c>'s focus advance (<c>Coord.as:64-77</c>):
        /// <c>++tr; if(tr &gt; 3) tr = 1; if(liv[tr]) break;</c> — <b>up to three increments</b>, so a
        /// dead commander is skipped and a table where all three are dead ends on the third candidate
        /// without looping forever.
        ///
        /// <para><b>The first increment happens even if the current commander is alive.</b> The
        /// rotation always moves on when the window expires; it never re-selects the same one.</para>
        /// </summary>
        public static int NextFocus(int currentTr, bool liv1, bool liv2, bool liv3)
        {
            int tr = currentTr;

            for (int i = 0; i < CommanderCount; i++)
            {
                tr++;
                if (tr > CommanderCount) tr = 1;
                if (IsAlive(tr, liv1, liv2, liv3)) break;
            }

            return tr;
        }

        /// <summary><c>this["liv" + tr]</c> (<c>Coord.as:72</c>) — <c>false</c> for any other tr.</summary>
        public static bool IsAlive(int tr, bool liv1, bool liv2, bool liv3)
        {
            if (tr == 1) return liv1;
            if (tr == 2) return liv2;
            if (tr == 3) return liv3;
            return false;
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  setLevel (UnitBossEncl.as:113-132)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>_loc3_ = 1; if(globalDif == 3) 1.2; if(globalDif == 4) 1.5;</c> (<c>:117-125</c>).
        ///
        /// <para>Applied to <c>hp = maxhp = hp * _loc3_</c>, <c>dam *= _loc3_</c> and
        /// <c>currentWeapon.damage *= _loc3_</c> (<c>:126-131</c>) — <b>not</b> to
        /// <c>damageExpl</c> and not to <c>armorhp</c>.</para>
        ///
        /// <para><b>Two independent <c>if</c>s, not a ladder — and exact equality.</b> The oracle tests
        /// <c>== 3</c> and <c>== 4</c> separately, so any other difficulty (including a hypothetical 5)
        /// leaves the factor at <c>1</c>. A <c>&gt;= 4</c> ladder would scale a difficulty the oracle
        /// leaves alone; the shipped ladder stops at 4, which is exactly why the difference would never
        /// show up in play and would only be a lie in the source.</para>
        ///
        /// <para>The port's spawner already applies AS3's <i>base</i> rule
        /// (<c>DifficultyRules.EnemyHpMultiplier</c>), which is 1 above difficulty 1 and only shrinks
        /// on easy — so this boss-specific 1.2/1.5 is the only high-difficulty scaling the commander
        /// gets, exactly as it was the alicorn's.</para>
        /// </summary>
        public static float DifficultyScale(int difficulty)
        {
            float scale = 1f;
            if (difficulty == 3) scale = 1.2f;
            if (difficulty == 4) scale = 1.5f;
            return scale;
        }

        /// <summary>
        /// <c>blitId = "sprEnclboss2"</c> for <c>tr == 2</c> (<c>:61</c>) and
        /// <c>"sprEnclboss3"</c> for <c>tr == 3</c> (<c>:72</c>). <c>null</c> means "keep the row's own
        /// sheet", which is <c>sprEnclboss1</c> for <c>tr == 1</c> and for any other <c>tr</c>.
        ///
        /// <para><b>The three commanders are three different bodies, and only one of them is in the
        /// data.</b> <c>AllData.as:642</c> authors <c>&lt;vis blit='sprEnclboss1'&gt;</c>; the other two
        /// sheets are named <i>only</i> in the class body, so an import of the node alone gives all
        /// three the same art. That is why this is a rule and not a field on the asset.</para>
        ///
        /// <para><b>Consumed by <c>BossEnclController.ApplyCommanderSheet</c>, not by the spawner.</b>
        /// The answer is a sheet <i>id</i>, and the row's primary sheet is
        /// <c>sprEnclboss1</c>, so tr1 needs no swap at all. tr2/tr3 resolve their grid from
        /// <c>bossencl.asset</c>'s <c>sheetVariants</c> (written by <c>UnitSpriteImporter</c>) and hand
        /// it to <c>UnitAnimator.SetSheetOverride</c>, which keeps every <c>BlitAnim</c> row and swaps
        /// only the pixels — the same thing AS3 does by assigning <c>blitId</c>.</para>
        /// </summary>
        public static string BlitIdFor(int tr)
        {
            if (tr == 2) return "sprEnclboss2";
            if (tr == 3) return "sprEnclboss3";
            return null;
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  animate() (UnitBossEncl.as:134-164)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The two rows this unit authors — <c>AllData.as:645-646</c> has only <c>stay</c>/<c>fly</c>.</summary>
        public const string FlyAnimId = "fly";

        /// <inheritdoc cref="FlyAnimId"/>
        public const string StayAnimId = "stay";

        /// <summary>
        /// <c>if(isFly) animState = "fly"; else animState = "stay";</c> (<c>:139-146</c>).
        ///
        /// <para><b>This unit is airborne for its whole life</b> (<c>isFly = true</c>, <c>:92</c>), so
        /// the live answer is always <c>"fly"</c>. The <c>"stay"</c> arm is unreachable in the oracle
        /// and is kept as a parameter so the rule is the oracle's branch rather than a constant.</para>
        ///
        /// <para><b>The corpse arm is the port's, not the oracle's.</b> <c>UnitBossEncl.animate()</c>
        /// never tests <c>sost</c>; a corpse would keep looping <c>fly</c> if the draw were still
        /// called. The unit authors no <c>die</c> row (<c>AllData.as:645-646</c> has only
        /// <c>stay</c>/<c>fly</c>), so the port picks the one-frame <c>stay</c> for a body — the same
        /// "the corpse stops moving" convention <c>EnemyBrain</c> documents for units with no death
        /// row.</para>
        /// </summary>
        public static string ResolveAnimState(bool alive, bool isFly)
        {
            if (!alive) return StayAnimId;
            return isFly ? FlyAnimId : StayAnimId;
        }
    }
}
