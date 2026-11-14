using System;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// What one <see cref="BossEnclCoord.Step"/> asks the caller to do to the arena's doors — AS3
    /// <c>Coord.step()</c>'s two <c>allAct</c> broadcasts (<c>Coord.as:80-99</c>).
    /// </summary>
    [Flags]
    public enum BossEnclCoordEvents
    {
        None = 0,

        /// <summary>
        /// <c>if(this.t2 == 75)</c> — tell each door <c>"red"</c> when it will open and <c>"green"</c>
        /// when it will close, so the arena telegraphs the coming change.
        /// </summary>
        SignalDoors = 1,

        /// <summary>
        /// <c>if(this.t2 == 0)</c> — actually open the open set and close the closed set, re-roll the
        /// set, and restart the 300-tick cycle.
        /// </summary>
        ToggleDoors = 2
    }

    /// <summary>
    /// AS3 <c>Coord</c> (<c>fe/unit/Coord.as</c>) — <b>one per room</b>, shared by the Enclave
    /// Commander's three bodies.
    ///
    /// <para><b>What it does.</b> It owns two timers and a three-way rotation. <c>t1</c> is the firing
    /// right: it counts down from 150..209, and when it expires it advances <c>tr</c> to the next
    /// <i>living</i> commander and resets. A commander may fire only while
    /// <c>tr == its own tr &amp;&amp; t1 &gt; 45</c> (<c>UnitBossEncl.as:306</c>), so exactly one of the
    /// three shoots at a time and it stops shooting 45 ticks before the right moves on. <c>t2</c> is
    /// the arena's door cycle: at 75 it flashes red/green, at 0 it opens/closes the six doors tagged
    /// <c>a1..a6</c> and re-rolls which three are shut.</para>
    ///
    /// <para><b>Why it is not a field on the boss.</b> <c>UnitBossEncl.die()</c> writes
    /// <c>coord["liv" + tr] = false</c> (<c>:99</c>) and <c>Coord.step()</c> reads it to skip the dead
    /// (<c>Coord.as:72</c>). A per-commander timer would have three copies of the rotation and would
    /// drift the moment one of them died — the trap <c>90_Boss_Implementation.md</c> §13.9 names.
    /// The oracle steps this object from <c>Location.step()</c> (<c>Location.as:3388-3390</c>), not
    /// from a boss; the port's equivalent is <see cref="LastSteppedTick"/>, which lets whichever
    /// commander ticks first in a frame advance it <b>exactly once</b>.</para>
    ///
    /// <para><b>Randomness is injected.</b> <c>Math.random()</c> is not available in a pure class, and
    /// a fixture that could not control the draw could not pin the rotation. The constructor takes an
    /// inclusive-max roll delegate; the live caller passes <c>UnityEngine.Random.Range(0, max + 1)</c>
    /// and a fixture passes a scripted sequence. Draws happen only in the arms that consume them
    /// (window expiry, door re-roll), so the order matches the oracle's.</para>
    /// </summary>
    public sealed class BossEnclCoord
    {
        /// <summary><c>this.tr = 1</c> (<c>Coord.as:35</c>).</summary>
        public int Tr = 1;

        /// <summary><c>this.t1 = 100</c> (<c>Coord.as:36</c>) — the first firing window.</summary>
        public int T1 = BossEnclRules.CoordT1Initial;

        /// <summary><c>this.t2 = 150</c> (<c>Coord.as:37</c>) — the first door cycle.</summary>
        public int T2 = BossEnclRules.CoordT2Initial;

        /// <summary><c>liv1</c> (<c>Coord.as:18</c>) — set by <c>putLoc</c>, cleared by <c>die()</c>.</summary>
        public bool Liv1;
        public bool Liv2;
        public bool Liv3;

        /// <summary>
        /// <c>opened[]</c> (<c>Coord.as:28</c>): <c>true</c> means door <c>a(i+1)</c> is open after the
        /// next toggle. <c>rndOpened</c> starts all six open and shuts three.
        /// </summary>
        public readonly bool[] Opened = new bool[BossEnclRules.DoorCount];

        /// <summary>
        /// The last simulation tick this object was stepped on. The brain calls
        /// <see cref="StepIfDue"/> from every commander, and this field is what makes the shared timer
        /// advance once per frame rather than three times.
        /// </summary>
        public int LastSteppedTick = int.MinValue;

        private readonly Func<int, int> _rollInclusive;

        /// <param name="rollInclusive">
        /// <c>max =&gt; [0, max]</c>. Null is tolerated and answers 0 — a deterministic object with
        /// doors 1..3 shut, which is the right default for a fixture that does not care about doors.
        /// </param>
        public BossEnclCoord(Func<int, int> rollInclusive = null)
        {
            _rollInclusive = rollInclusive ?? (_ => 0);

            for (int i = 0; i < Opened.Length; i++)
            {
                Opened[i] = true;
            }

            // `this.rndOpened()` in the constructor (Coord.as:38).
            RandomizeDoors();
        }

        /// <summary><c>this["liv" + tr]</c> (<c>Coord.as:72</c>).</summary>
        public bool IsAlive(int tr) => BossEnclRules.IsAlive(tr, Liv1, Liv2, Liv3);

        /// <summary>
        /// <c>putLoc</c> writes <c>true</c> and <c>die()</c> writes <c>false</c>
        /// (<c>UnitBossEncl.as:99,110</c>).
        /// </summary>
        public void SetAlive(int tr, bool alive)
        {
            if (tr == 1) Liv1 = alive;
            else if (tr == 2) Liv2 = alive;
            else if (tr == 3) Liv3 = alive;
        }

        /// <summary><c>coord.tr == this.tr &amp;&amp; coord.t1 &gt; 45</c> (<c>UnitBossEncl.as:306</c>).</summary>
        public bool CanFire(int commanderTr) => BossEnclRules.ShouldFire(Tr, T1, commanderTr);

        /// <summary>
        /// <c>Coord.step()</c> (<c>Coord.as:57-100</c>), once per frame.
        ///
        /// <para><b>Order is the oracle's.</b> <c>t1</c> first (so a window that expires this tick
        /// already hands the right to the next commander before anyone attacks), then <c>t2</c>.
        /// The door re-roll happens <i>after</i> the toggle, so the verbs use the set that was
        /// current for the window just ended and the next window uses the new one.</para>
        /// </summary>
        public BossEnclCoordEvents Step()
        {
            BossEnclCoordEvents events = BossEnclCoordEvents.None;

            // `--this.t1; if(this.t1 <= 0) { this.t1 = floor(random*60+150); ++tr ... }` (:60-78).
            T1--;
            if (T1 <= 0)
            {
                T1 = BossEnclRules.CoordT1Base + _rollInclusive(BossEnclRules.CoordT1RollRange - 1);
                Tr = BossEnclRules.NextFocus(Tr, Liv1, Liv2, Liv3);
            }

            // `--this.t2; if(this.t2 == 75) { ... "red"/"green" ... } if(this.t2 == 0) { ... }` (:79-99).
            T2--;
            if (T2 == BossEnclRules.CoordSignalTick)
            {
                events |= BossEnclCoordEvents.SignalDoors;
            }

            if (T2 == 0)
            {
                events |= BossEnclCoordEvents.ToggleDoors;
                T2 = BossEnclRules.CoordT2Cycle;
                RandomizeDoors();
            }

            return events;
        }

        /// <summary>
        /// <c>this.loc.allAct(null, this.opened[i] ? "red" : "green", "a" + i)</c> (<c>Coord.as:85</c>)
        /// — the telegraph broadcast at <c>t2 == 75</c>.
        ///
        /// <para><b><c>red</c> means "this door is in the open set", not "this door is closed".</b> The
        /// verb is chosen by the same <see cref="Opened"/> slot the toggle will later act on, so the two
        /// halves cannot disagree; a port that inverted either one would flash the arena's doors in
        /// reverse for the whole fight and no number would look wrong.</para>
        /// </summary>
        public string DoorSignalVerb(int index) => Opened[index] ? "red" : "green";

        /// <summary>
        /// <c>this.loc.allAct(null, this.opened[i] ? "open" : "close", "a" + i)</c>
        /// (<c>Coord.as:94</c>) — the broadcast at <c>t2 == 0</c>.
        /// </summary>
        public string DoorCommandVerb(int index) => Opened[index] ? "open" : "close";

        /// <summary>
        /// The <c>allid</c> tag for slot <paramref name="index"/>: <c>"a1"</c>..<c>"a6"</c>.
        ///
        /// <para><b>One-based on purpose.</b> AS3's array is indexed 1..6 and the tag is built by
        /// concatenation, so slot 0 is <c>a1</c>. The port's array is zero-based, and this is the only
        /// place the offset exists.</para>
        /// </summary>
        public static string DoorTag(int index) =>
            "a" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// <see cref="Step"/> at most once for a given simulation tick — the shared-timer guard.
        /// Returns <see cref="BossEnclCoordEvents.None"/> when another commander already stepped it.
        /// </summary>
        public BossEnclCoordEvents StepIfDue(int tickIndex)
        {
            if (LastSteppedTick == tickIndex) return BossEnclCoordEvents.None;

            LastSteppedTick = tickIndex;
            return Step();
        }

        /// <summary>
        /// <c>rndOpened()</c> (<c>Coord.as:41-55</c>): every door open, then
        /// <c>kolClosed = 3</c> draws of <c>floor(random*6+1)</c> shut one each.
        ///
        /// <para><b>Duplicates are possible and are the oracle's.</b> Three draws from six slots do not
        /// have to be distinct, so a cycle can shut two doors rather than three. Reproduced rather than
        /// "fixed" to a shuffle: the fight's arena is visibly irregular in the original for the same
        /// reason.</para>
        /// </summary>
        public void RandomizeDoors()
        {
            for (int i = 0; i < Opened.Length; i++)
            {
                Opened[i] = true;
            }

            for (int i = 0; i < BossEnclRules.ClosedDoorCount; i++)
            {
                Opened[_rollInclusive(BossEnclRules.DoorCount - 1)] = false;
            }
        }
    }
}
