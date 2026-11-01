using System.Collections.Generic;
using UnityEngine;
using PFE.Core;
using PFE.Systems.Map.TileQuery;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Which of AS3's three hit-number modes is live — <c>World.showHit</c>
    /// (<c>World.as:214</c>), set from the PipBuck options page
    /// (<c>PipPageOpt.as:563-573</c>: <c>showHit = hit1 ? (hit2 ? 2 : 1) : 0</c>).
    /// </summary>
    public enum HitNumberStyle
    {
        /// <summary>Nothing is printed. AS3 <c>showHit == 0</c>.</summary>
        Off = 0,

        /// <summary>One figure per hit. AS3 <c>showHit == 1</c>.</summary>
        PerHit = 1,

        /// <summary>
        /// One figure per target that <b>adds the damage up</b> while the hits keep landing. AS3
        /// <c>showHit == 2</c>, and <b>the oracle's default</b> (<c>World.as:214</c> is
        /// <c>showHit:int = 2</c>).
        /// </summary>
        Accumulate = 2,
    }

    /// <summary>
    /// The oracle's <c>visualNumb</c> frames, 1-based. Each is a whole colour of the same figure —
    /// <c>Unit.damage()</c> picks one per cast and the sprite's text field does the rest.
    /// </summary>
    /// <remarks>
    /// The indices are the oracle's <c>frame:</c> argument verbatim, so a cast site can be read against
    /// AS3 line for line. <c>9</c> is unused by any cast in the oracle and is deliberately absent.
    /// </remarks>
    public static class HitNumberFrame
    {
        /// <summary>Damage on anything that is not the player. Orange. <c>Unit.as:3908</c>.</summary>
        public const int Damage = 1;

        /// <summary>
        /// Damage on the <b>player</b>, or any <b>stealth</b> crit on anyone — AS3 collapses those two
        /// into one branch (<c>this.player || _loc5_ &gt;= 2</c>, <c>Unit.as:3916-3919</c>). Red.
        /// </summary>
        public const int PlayerDamage = 2;

        /// <summary>
        /// Ambient damage: <c>param4</c> is <c>true</c>, which is the path bleed, poison, burning,
        /// acid, radiation and <c>D_INSIDE</c> take (<c>Unit.as:3920-3923</c>). Purple. It is also the
        /// only frame that prints while <c>showHit</c> is off (<c>:3928</c>).
        /// </summary>
        public const int Ambient = 3;

        /// <summary>A heal, <c>+N</c>. Green. <c>Unit.as:4052-4057</c>.</summary>
        public const int Heal = 4;

        /// <summary>A status word — "burning", "armour broken", "stunned". Yellow-green. <c>Unit.as:3976-3981</c>.</summary>
        public const int Message = 5;

        /// <summary>Mana restored, <c>+N</c>. Cyan. <c>Pers.as:1923-1928</c>.</summary>
        public const int ManaGain = 6;

        /// <summary>Mana drained, <c>-N</c>. Yellow. <c>UnitPlayer.as:3204-3209</c>.</summary>
        public const int ManaLoss = 7;

        /// <summary>Experience, <c>+Nxp</c>, at scale 1.5 and half opacity. Cream. <c>Pers.as:979-984</c>.</summary>
        public const int Xp = 8;

        /// <summary>An evaded hit. Grey, at half opacity. <c>Unit.as:4114-4119</c>.</summary>
        public const int Miss = 10;

        /// <summary>Damage on the player from <c>D_PINK</c>. Pink. <c>Unit.as:3924-3927</c>.</summary>
        public const int Pink = 11;
    }

    /// <summary>
    /// One floating figure, as the overlay needs to see it: where it is, what it says, how old it is.
    /// </summary>
    /// <remarks>
    /// A struct, and it holds no colour of its own — <see cref="Frame"/> is the oracle's sprite frame
    /// index and the view maps it to a tint. Keeping the visual choice out of the data is what lets
    /// <see cref="DamageEventFeed"/> be tested without a renderer.
    /// </remarks>
    public readonly struct DamageNumber
    {
        /// <summary>Impact point, in world space — the same point <c>DamageDealtMessage.position</c> carries.</summary>
        public readonly Vector3 Position;

        /// <summary>
        /// HP damage. In <see cref="HitNumberStyle.Accumulate"/> this is the <b>running sum</b> for the
        /// target, not the last hit — AS3's <c>hitSumm</c> (<c>Unit.as:3940</c>).
        /// </summary>
        public readonly float Amount;

        public readonly bool IsCritical;
        public readonly bool IsMiss;

        /// <summary>Seconds since this figure was created — reset to zero on every accumulating hit.</summary>
        public readonly float Age;

        public readonly string CustomText;
        public readonly Color? CustomColor;

        /// <summary>
        /// The oracle's <c>visualNumb</c> frame, 1-based: <c>1</c> damage, <c>2</c> player / stealth
        /// crit, <c>3</c> ambient, <c>4</c> heal, <c>5</c> status text, <c>6</c> mana gain, <c>7</c>
        /// mana loss, <c>8</c> xp, <c>10</c> miss, <c>11</c> player + pink. <c>0</c> = unspecified.
        /// </summary>
        public readonly int Frame;

        /// <summary>
        /// The figure's scale. AS3 sets <c>1.6</c> for an ordinary crit (<c>_loc5_ == 1 || 3</c>,
        /// <c>Unit.as:3935</c>) and leaves it at <c>1</c> otherwise. Divided by the camera's zoom in the
        /// oracle because its figure is a world object; the port's overlay is already screen-space.
        /// </summary>
        public readonly float Scale;

        /// <summary>
        /// Which target this figure belongs to, or <c>0</c> for a figure that never accumulates. This is
        /// AS3's <c>Unit.hitPart</c> — the per-unit handle the accumulate mode updates in place
        /// (<c>Unit.as:3941-3958</c>).
        /// </summary>
        public readonly int TargetKey;

        public DamageNumber(
            Vector3 position, float amount, bool isCritical, bool isMiss, float age,
            string customText = null, Color? customColor = null,
            int frame = 0, float scale = 1f, int targetKey = 0)
        {
            Position = position;
            Amount = amount;
            IsCritical = isCritical;
            IsMiss = isMiss;
            Age = age;
            CustomText = customText;
            CustomColor = customColor;
            Frame = frame;
            Scale = scale;
            TargetKey = targetKey;
        }
    }

    /// <summary>
    /// A small bounded log of recent hits, written by <c>DamageSystem</c> and drained by the
    /// floating-damage overlay.
    ///
    /// <para><b>It models AS3's <c>numbEmit</c>, not "one number per hit".</b> The oracle's default
    /// (<c>World.showHit = 2</c>) does not print a figure per hit: it keeps <b>one</b> figure per target
    /// and adds every further hit into it, so a burst reads as a single number climbing — that is
    /// <c>Unit.hitSumm += param1</c> at <c>Unit.as:3940</c>, with the figure's lifetime refreshed to
    /// <c>60</c> frames on each hit (<c>:3957</c>) and the sum reset after ten quiet frames
    /// (<c>:3222-3233</c>). <see cref="HitNumberStyle.PerHit"/> is the other mode the options page can
    /// select; it prints one figure per hit as the port used to always do.</para>
    ///
    /// <para><b>Why a feed and not a direct call into the overlay.</b> <c>DamageSystem</c> lives in the
    /// simulation and must not know a debug overlay exists; the overlay self-bootstraps from a scene-load
    /// hook and has no DI container to resolve a subscriber from. A tiny push/pull buffer decouples them
    /// in both directions: the simulation writes four values, the view reads what it likes, and either
    /// can be absent. This is also why the buffer is bounded and drops the oldest entry rather than
    /// growing — a diagnostic that allocates without limit while you hold down the fire button is worse
    /// than the bug it was opened to find.</para>
    ///
    /// <para><b>The default instance is shared; tests should not use it.</b> <see cref="Default"/> is a
    /// process-wide singleton so the overlay can read it without wiring, which means state written by
    /// one test would leak into the next. Every test constructs its own.</para>
    ///
    /// <para><b>One oracle detail is deliberately not modelled: <c>visDamDY</c>.</b> AS3 subtracts
    /// <c>15</c> from a per-unit vertical offset on every cast and zeroes it at the end of the unit's own
    /// <c>step()</c> (<c>Unit.as:3930</c>, <c>:1860</c>), so several casts in one frame stack 15 px
    /// apart. That counter is reset per <i>unit step</i>, which this feed has no notion of — the honest
    /// port would reset it per sim tick, and there is no tick here to hook. The ±20 px horizontal jitter
    /// every cast already carries keeps same-frame figures from landing on top of each other, so the
    /// omission costs a little vertical separation and nothing else.</para>
    /// </summary>
    public sealed class DamageEventFeed
    {
        /// <summary>Enough for a burst of automatic fire without dropping the frame's first hits.</summary>
        public const int DefaultCapacity = 64;

        /// <summary>
        /// How long a figure lives: AS3 <c>MinLiv = 60</c> frames on the <c>numb</c> emitter
        /// (<c>ParticleDefinitions</c> <c>Id: numb</c>, from the oracle's <c>&lt;part id="numb"&gt;</c>),
        /// at 30 frames/second.
        /// </summary>
        public const int LifetimeFrames = 60;

        /// <summary><see cref="LifetimeFrames"/> in seconds — 2.0.</summary>
        public const float DefaultLifetime = LifetimeFrames / (float)SimClock.CanonicalTicksPerSecond;

        /// <summary>
        /// How long a target keeps accumulating. AS3's <c>t_hitPart</c> is set to <c>10</c> on every
        /// landing hit and counted down once per unit step; when it reaches zero the sum is cleared and
        /// the handle dropped (<c>Unit.as:3222-3233</c>). Ten frames at 30 Hz.
        /// </summary>
        public const int AccumulateWindowFrames = 10;

        /// <summary><see cref="AccumulateWindowFrames"/> in seconds — 1/3.</summary>
        public const float AccumulateWindow =
            AccumulateWindowFrames / (float)SimClock.CanonicalTicksPerSecond;

        /// <summary>
        /// The <c>numb</c> emitter's <c>DY</c>: it starts one pixel per frame <i>up</i>
        /// (<c>DY: -1</c>, and AS3's Y runs down, so the sign flips in Unity space).
        /// </summary>
        public const float RisePixelsPerFrame = 1f;

        /// <summary>
        /// The <c>numb</c> emitter's <c>Brake</c>. AS3's <c>Part.step</c> does
        /// <c>Y += dy; dy *= brake</c> (<c>Part.as:161-169</c>), so the figure decelerates: it climbs
        /// fast, then settles. Not a linear ramp — the port's first version was linear and it read as
        /// "a number being dragged up the screen".
        /// </summary>
        public const float RiseBrake = 0.98f;

        /// <summary>
        /// The figure's horizontal jitter, <c>rx: 40</c> in every damage cast — the emitter spreads it
        /// as <c>(random - 0.5) * rx</c>, so the figure lands within ±20 px
        /// (<c>Emitter.as:232-237</c>).
        /// </summary>
        /// <remarks>
        /// <b>This is the FULL width, and it is the number the formula multiplies.</b> The first
        /// version of this constant was <c>JitterHalfWidthPixels = 20</c> used <i>as</i> the multiplier,
        /// which silently halved the spread to ±10 px — half the oracle's. Nothing caught it because the
        /// only assertion on the spread was an upper bound (<c>&lt;= 0.2</c> units), and ±0.1 satisfies
        /// ±0.2. A bound that both the right and the wrong value pass is not a test; the offline jitter
        /// fixture now asserts the low end of the range as well.
        /// </remarks>
        public const float JitterWidthPixels = 40f;

        /// <summary>±20 px — the bound the figure actually lands in, derived so the two cannot drift.</summary>
        public const float JitterHalfWidthPixels = JitterWidthPixels * 0.5f;

        /// <summary>AS3 <c>_loc5_ == 1 || _loc5_ == 3 ? 1.6 : 1</c> (<c>Unit.as:3935</c>).</summary>
        public const float CriticalScale = 1.6f;

        /// <summary>
        /// The opacity a miss is cast with — <c>"alpha": 0.5</c> at <c>Unit.as:4118</c>. A miss is a
        /// non-event, and AS3 draws it half-strength so it does not compete with a real number.
        /// </summary>
        public const float MissOpacity = 0.5f;

        /// <summary>The xp figure's scale — <c>"scale": 1.5</c> at <c>Pers.as:983</c>.</summary>
        public const float XpScale = 1.5f;

        /// <summary>
        /// AS3's <c>param1 &gt; 0.5</c> gate on printing a damage figure (<c>Unit.as:3683</c>).
        /// </summary>
        /// <remarks>
        /// <b>The feed does not apply it, and that is deliberate.</b> The oracle's gate means a hit that
        /// armour absorbed completely prints nothing — which is indistinguishable on screen from a shot
        /// that never collided, and that distinction is the entire reason this overlay exists (see
        /// <c>FloatingDamageOverlay</c>). So the feed records the absorbed hit and the view draws
        /// <c>0</c> in its own colour. A renderer that wants the oracle's silence can test this
        /// constant.
        /// </remarks>
        public const float MinPrintedDamage = 0.5f;

        /// <summary>The process-wide feed the overlay reads. See the class remarks.</summary>
        public static DamageEventFeed Default { get; } = new DamageEventFeed();

        private readonly List<DamageNumber> _live = new List<DamageNumber>();
        private readonly int _capacity;
        private readonly float _lifetime;

        /// <summary>
        /// The jitter source. <see cref="System.Random"/>, deliberately, not
        /// <c>UnityEngine.Random</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the only line that used to make the whole feed untestable.</b>
        /// <c>UnityEngine.Random.value</c> is a native call, and a native call is an <c>ECall</c>
        /// outside the player — so every one of the sixteen tests that call <see cref="Report"/> died
        /// in <c>Jitter</c> with <c>SecurityException: ECall methods must be packaged into a system
        /// module</c>, while the fifteen that only touch the static view maths passed. The result was
        /// a fixture that looked half-green and proved nothing about the accumulate behaviour, which is
        /// the one thing it exists to pin.</para>
        ///
        /// <para>Nothing needs Unity's generator here. The feed is a bounded list of structs and the
        /// spread is one uniform draw; <c>System.Random</c> is managed, so the type is now engine-free
        /// and the fixture runs in the shell. It also makes the spread <i>reproducible</i>, which for a
        /// debug overlay is a feature: a seeded feed replays a burst identically.</para>
        /// </remarks>
        private readonly System.Random _rng;

        /// <param name="seed">
        /// Fixed jitter seed, or <c>null</c> for a time-seeded one. Tests pass a seed so the ±20 px
        /// spread is assertable; production passes nothing.
        /// </param>
        public DamageEventFeed(int capacity = DefaultCapacity, float lifetime = DefaultLifetime,
                               int? seed = null)
        {
            _capacity = Mathf.Max(1, capacity);
            _lifetime = Mathf.Max(0.01f, lifetime);
            _rng = seed.HasValue ? new System.Random(seed.Value) : new System.Random();
        }

        /// <summary>
        /// The tint for one of the oracle's <c>visualNumb</c> frames.
        /// </summary>
        /// <remarks>
        /// <para><b>These are sampled from the shipped art, not chosen.</b> The numbers are the most
        /// saturated pixel of <c>Assets/_PFE/Art/Particles/visualNumb/f000..f010.png</c>, which is the
        /// oracle's own sprite sheet — so the overlay matches the game it is diagnosing rather than
        /// inventing a palette beside it. Frame <c>n</c> is file <c>f(n-1)</c>.</para>
        ///
        /// <para>An unknown frame falls back to <see cref="HitNumberFrame.Damage"/>'s orange, which is
        /// what a damage figure is when nobody said otherwise.</para>
        /// </remarks>
        public static Color ColorFor(int frame)
        {
            switch (frame)
            {
                case HitNumberFrame.PlayerDamage: return new Color32(255, 102, 102, 255);
                case HitNumberFrame.Ambient:      return new Color32(200, 108, 255, 255);
                case HitNumberFrame.Heal:         return new Color32(102, 255, 0, 255);
                case HitNumberFrame.Message:      return new Color32(180, 255, 22, 255);
                case HitNumberFrame.ManaGain:     return new Color32(36, 214, 255, 255);
                case HitNumberFrame.ManaLoss:     return new Color32(255, 255, 0, 255);
                case HitNumberFrame.Xp:           return new Color32(255, 225, 152, 255);
                case HitNumberFrame.Miss:         return new Color32(153, 153, 153, 255);
                case HitNumberFrame.Pink:         return new Color32(251, 176, 247, 255);
                default:                          return new Color32(255, 146, 13, 255);
            }
        }

        /// <summary>
        /// The live mode. Defaults to <see cref="HitNumberStyle.Accumulate"/> because that is the
        /// oracle's own default (<c>World.as:214</c>).
        /// </summary>
        public HitNumberStyle Style { get; set; } = HitNumberStyle.Accumulate;

        /// <summary>Entries currently held, including ones already past their lifetime.</summary>
        public int Count => _live.Count;

        public float Lifetime => _lifetime;

        /// <summary>
        /// Figures that are currently adding up — one per target inside its ten-frame window. Read by the
        /// overlay's legend, and the one number that says at a glance whether the accumulate mode is
        /// actually running.
        /// </summary>
        public int AccumulatingCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _live.Count; i++)
                {
                    if (_live[i].TargetKey != 0 && !_live[i].IsMiss && _live[i].Age < AccumulateWindow)
                        count++;
                }

                return count;
            }
        }

        /// <summary>
        /// Record a hit. <paramref name="amount"/> is HP damage as applied, so a fully-absorbed hit
        /// shows <c>0</c> — which is a finding, not a reason to skip the entry: "the shot connected and
        /// did nothing" is exactly what an armour bug looks like.
        /// </summary>
        /// <param name="targetKey">
        /// Identifies the target, so successive hits on it add up. <c>0</c> means "no target" — such an
        /// entry never accumulates, which is what a caller with no target (a test, or a figure that is
        /// not about a unit) wants.
        /// </param>
        public void Report(Vector3 position, float amount, bool isCritical, bool isMiss,
                           int targetKey = 0, int frame = 0)
        {
            if (Style == HitNumberStyle.Off)
                return;

            // ── The miss branch ─────────────────────────────────────────────────────────────────
            // AS3 takes a separate path for an evaded hit (`Unit.as:4108-4121`): it prints only when
            // `showHit == 1 || showHit == 2 && t_hitPart == 0` — so in accumulate mode a miss during an
            // active burst prints nothing, because the running total is the story — and it then sets
            // `t_hitPart = 10`, which keeps a second miss inside the same ten frames from printing too.
            // It never touches `hitSumm` or `hitPart`: a miss is not a hit on the total.
            //
            // The miss figure carries the target key so that `t_hitPart = 10` has somewhere to live —
            // see FindFigureFor, which can count a miss as "something happened here recently" without
            // ever letting a damage hit accumulate into it.
            if (isMiss)
            {
                bool printing = Style == HitNumberStyle.PerHit
                                || !FindFigureFor(targetKey, includeMiss: true, out _);

                if (printing)
                {
                    Add(new DamageNumber(
                        Jitter(position), 0f, isCritical: false, isMiss: true, age: 0f,
                        customText: null, customColor: null, frame: frame, targetKey: targetKey));
                }

                return;
            }

            if (Style == HitNumberStyle.Accumulate && targetKey != 0)
            {
                // Still inside the window, and the figure it points at is still in the list: AS3 updates
                // that same part — sums, refreshes `liv` to 60, and leaves the position alone, so the
                // number keeps drifting from where it was born rather than jumping to the newest hit.
                if (FindFigureFor(targetKey, includeMiss: false, out int index))
                {
                    DamageNumber existing = _live[index];

                    _live[index] = new DamageNumber(
                        existing.Position,
                        existing.Amount + amount,
                        existing.IsCritical || isCritical,
                        existing.IsMiss,
                        age: 0f,
                        existing.CustomText,
                        existing.CustomColor,
                        // AS3 never repaints the frame on an update — only the scale, and only upwards
                        // (`Unit.as:3952-3955`). A crit that arrives third still enlarges the figure.
                        existing.Frame,
                        isCritical ? CriticalScale : existing.Scale,
                        targetKey);

                    return;
                }

                Add(new DamageNumber(
                    Jitter(position), amount, isCritical, isMiss: false, age: 0f,
                    customText: null, customColor: null,
                    frame: frame, scale: isCritical ? CriticalScale : 1f, targetKey: targetKey));

                return;
            }

            Add(new DamageNumber(
                Jitter(position), amount, isCritical, isMiss: false, age: 0f,
                customText: null, customColor: null,
                frame: frame, scale: isCritical ? CriticalScale : 1f, targetKey: 0));
        }

        /// <summary>
        /// Record an arbitrary floating text notification — the oracle's other <c>numbEmit</c> casts:
        /// heal (<c>+N</c>, frame 4), xp (<c>+Nxp</c>, frame 8, scale 1.5), a status word (frame 5).
        /// </summary>
        /// <remarks>
        /// Never accumulates: AS3 casts a fresh part for each of these and never holds a handle to it,
        /// so two heals a frame apart are two figures. The <paramref name="jitter"/> is on by default
        /// because every one of those casts passes <c>rx: 20</c>.
        /// </remarks>
        public void ReportText(Vector3 position, string text, Color? color = null, int frame = 0,
                               float scale = 1f, bool jitter = true)
        {
            if (Style == HitNumberStyle.Off)
                return;

            Add(new DamageNumber(
                jitter ? Jitter(position) : position, 0f, isCritical: false, isMiss: false, age: 0f,
                customText: text, customColor: color, frame: frame, scale: scale));
        }

        /// <summary>
        /// Age every entry by <paramref name="deltaTime"/>, count down the accumulate windows, drop the
        /// expired, and copy the survivors into <paramref name="into"/>. Returns how many survived.
        /// </summary>
        /// <remarks>
        /// Ageing happens here, once per view refresh, rather than by storing an absolute birth time —
        /// so a paused or backgrounded game does not expire the whole buffer at once, and a test can
        /// advance time without a clock.
        /// </remarks>
        public int AdvanceAndSnapshot(float deltaTime, List<DamageNumber> into)
        {
            if (into == null) return 0;

            into.Clear();

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                float age = _live[i].Age + deltaTime;

                if (age >= _lifetime)
                {
                    _live.RemoveAt(i);
                    continue;
                }

                _live[i] = WithAge(_live[i], age);
            }

            if (_live.Count > 0)
                into.AddRange(_live);

            return into.Count;
        }

        public void Clear() => _live.Clear();

        // ── Internals ────────────────────────────────────────────────────────

        private void Add(in DamageNumber number)
        {
            if (_live.Count >= _capacity)
            {
                // Drop the oldest rather than refuse the newest: under sustained fire the recent hits
                // are the ones being diagnosed.
                _live.RemoveAt(0);
            }

            _live.Add(number);
        }

        /// <summary>
        /// The figure that belongs to <paramref name="targetKey"/> and is still inside the accumulate
        /// window, if there is one.
        /// </summary>
        /// <remarks>
        /// <para><b>There is no window counter, and that is not a shortcut.</b> AS3 resets
        /// <c>t_hitPart = 10</c> and <c>hitPart.liv = 60</c> on the same line of the same block
        /// (<c>Unit.as:3957</c>), so "the last hit on this target was under ten frames ago" and "this
        /// figure is younger than ten frames" are the same statement. The figure's own
        /// <see cref="DamageNumber.Age"/> therefore <i>is</i> the window — which is why nothing here has
        /// to count one down, and why the feed cannot leak a per-target timer that no view drains.</para>
        ///
        /// <para>Scanned rather than indexed: eviction and expiry both renumber the list, so a stored
        /// index would silently point at another unit's figure after the first eviction. Scanned from
        /// the end, so the newest figure for a target wins when an older one is still on screen.</para>
        /// </remarks>
        /// <param name="includeMiss">
        /// Count a recent <i>miss</i> as satisfying the window. The damage path must not (a total never
        /// absorbs into a MISS figure), but the miss path must — AS3's <c>t_hitPart = 10</c> after a
        /// printed miss is exactly what suppresses a second miss in the same ten frames.
        /// </param>
        private bool FindFigureFor(int targetKey, bool includeMiss, out int index)
        {
            index = -1;
            if (targetKey == 0) return false;

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                if (_live[i].TargetKey != targetKey) continue;
                if (_live[i].IsMiss && !includeMiss) continue;

                index = i;
                return _live[i].Age < AccumulateWindow;
            }

            return false;
        }

        private static DamageNumber WithAge(in DamageNumber number, float age)
            => new DamageNumber(
                number.Position, number.Amount, number.IsCritical, number.IsMiss, age,
                number.CustomText, number.CustomColor, number.Frame, number.Scale, number.TargetKey);

        /// <summary>
        /// AS3's <c>rx: 40</c> spread — <c>(Math.random() - 0.5) * 40</c>, i.e. ±20 px, horizontal only.
        /// </summary>
        /// <remarks>
        /// <c>rx</c> is the only axis every <c>numbEmit</c> cast passes (<c>Unit.as:3933</c>,
        /// <c>:979</c>, <c>:4032</c>), so the vertical component is exactly zero and the figure never
        /// drifts sideways twice. The draw is instance state, not a static call, so two feeds in one
        /// process cannot desynchronise each other's spread.
        /// </remarks>
        private Vector3 Jitter(Vector3 position)
        {
            float dx = ((float)_rng.NextDouble() - 0.5f) * JitterWidthPixels * TileQueryConstants.PixelToUnit;
            return position + new Vector3(dx, 0f, 0f);
        }

        // ── Pure view maths ──────────────────────────────────────────────────

        /// <summary>
        /// How far the figure has climbed after <paramref name="ageFrames"/> frames, in AS3 pixels.
        ///
        /// <para>AS3 integrates <c>Y += dy; dy *= 0.98</c> once per frame (<c>Part.as:161-169</c>), so
        /// after <c>n</c> frames the figure has moved the geometric series
        /// <c>1 + 0.98 + … + 0.98^(n-1) = (1 - 0.98^n) / 0.02</c> pixels — about <b>35 px</b> over the
        /// figure's whole life, most of it in the first half-second.</para>
        /// </summary>
        public static float RiseOffsetPixels(float ageFrames)
        {
            if (ageFrames <= 0f) return 0f;

            float frames = Mathf.Min(ageFrames, LifetimeFrames);
            return RisePixelsPerFrame * (1f - Mathf.Pow(RiseBrake, frames)) / (1f - RiseBrake);
        }

        /// <summary><see cref="RiseOffsetPixels"/> in Unity world units, for a figure aged in seconds.</summary>
        public static float RiseOffsetWorld(float ageSeconds)
            => RiseOffsetPixels(ageSeconds * SimClock.CanonicalTicksPerSecond)
               * TileQueryConstants.PixelToUnit;

        /// <summary>
        /// Opacity at <paramref name="age"/>. <b>Always 1</b>: the <c>numb</c> emitter has
        /// <c>Alph: 0</c> and <c>PreAlph: 0</c>, so the oracle never fades a damage figure — it
        /// disappears when its 60 frames run out. Kept as a named call so the overlay has one place to
        /// read and so the rule is stated rather than implied.
        /// </summary>
        public static float Alpha(float age, float lifetime) => 1f;

        /// <summary>
        /// The text for one entry. A miss reads <c>MISS</c> rather than <c>0</c> on purpose: the two
        /// have different causes — one is the evasion roll, the other is armour absorbing everything —
        /// and a shared <c>0</c> would hide which happened.
        /// </summary>
        /// <remarks>
        /// Damage is <c>Mathf.Round</c>, which is AS3's <c>Math.round(param1)</c>
        /// (<c>Unit.as:3932</c> and <c>:3956</c>) — not a ceiling. The port used to ceil so a graze
        /// would not read as <c>0</c>; the oracle rounds, and <c>0.5</c> is already the floor at which it
        /// prints anything at all (<see cref="MinPrintedDamage"/>).
        /// </remarks>
        public static string TextFor(in DamageNumber number)
        {
            if (!string.IsNullOrEmpty(number.CustomText)) return number.CustomText;
            if (number.IsMiss) return "MISS";

            return Mathf.Round(number.Amount).ToString();
        }
    }
}
