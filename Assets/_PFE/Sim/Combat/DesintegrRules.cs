using PFE.Data.Definitions;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Which of AS3's seven <c>Desintegr</c> presets a body dissolves with — <c>initBurn(param1)</c>'s
    /// argument, i.e. <c>Unit.die()</c>'s <b>death visual code</b>.
    /// </summary>
    /// <remarks>
    /// <para><b>This is not the damage type.</b> <c>Unit.as:3713-3741</c> translates the killing blow's
    /// damage type into one of these small integers at the moment HP crosses zero, and
    /// <c>die(param1)</c> only ever sees the integer. Two different damage types therefore share a tip
    /// (<c>D_FIRE</c> and <c>D_LASER</c> are both <c>1</c> on an overkill), and most damage types
    /// produce <c>0</c> — an ordinary corpse that does not dissolve at all.</para>
    ///
    /// <para><b><c>0</c> is not "tip zero", it is "no dissolve".</b> <c>param1 == 0</c> is the value that
    /// lets <c>die()</c> fall through to <c>replic("die")</c> (the death animation) instead of
    /// <c>initBurn</c>, so <see cref="None"/> must never reach the effect. The controller treats it as
    /// "leave the body alone".</para>
    ///
    /// <para><b><c>4 + blood</c> is the only arm the port can currently reach.</b> The overkill arm
    /// (<c>Unit.as:4347-4353</c>) is the one that fires when a body has taken twice its own maximum
    /// health past death, and it picks the tip straight off the unit's blood colour. The four
    /// damage-type tips are declared here because they are the oracle's table and because a caller that
    /// has the killing blow's damage type must be able to ask for one — see
    /// <see cref="DesintegrRules"/>'s note on why nothing passes them yet.</para>
    /// </remarks>
    public enum DesintegrTip
    {
        /// <summary>No dissolve — <c>die(0)</c>, the ordinary-corpse arm.</summary>
        None = 0,

        /// <summary>
        /// <c>burn</c> — orange, <c>GlowFilter(0xFFAA00)</c>, particles <c>burn</c>, sound
        /// <c>desintegr_f</c>. Reached by a <c>D_FIRE</c>/<c>D_LASER</c> overkill
        /// (<c>Unit.as:3713-3728</c>).
        /// </summary>
        Burn = 1,

        /// <summary>
        /// <c>plakap</c> — green, <c>GlowFilter(0x00FF00)</c>, particles <c>plakap</c>, sound
        /// <c>liquid_f</c>. Reached by <c>D_PLASMA</c> and <c>D_ACID</c>, unconditionally.
        /// </summary>
        Plasma = 2,

        /// <summary>
        /// <c>burn</c> again, but a blue <c>GlowFilter(0x4444FF)</c> and a mixed RGB ramp.
        /// <c>D_ASTRO</c> and <c>D_FRIEND</c>.
        /// </summary>
        Astro = 3,

        /// <summary>
        /// <c>krupa</c> — blue-white <c>GlowFilter(0x0000FF)</c>, particles <c>krupa</c>, sound
        /// <c>freezing_f</c>. <c>D_CRIO</c>.
        /// </summary>
        Cryo = 4,

        /// <summary><c>blood</c> — red, <c>blood=1</c>. The overkill arm for a red-blooded unit.</summary>
        BloodRed = 5,

        /// <summary><c>gblood</c> — green, <c>blood=2</c> (zombies).</summary>
        BloodGreen = 6,

        /// <summary><c>pblood</c> — pink, <c>blood=3</c>.</summary>
        BloodPink = 7
    }

    /// <summary>
    /// The per-channel colour transform a dissolve applies to the body — AS3's <c>ColorTransform</c>
    /// as a plain value, so the rules stay free of any engine type.
    /// </summary>
    /// <remarks>
    /// <para><b>Two halves, and the distinction is the oracle's.</b> Flash's <c>ColorTransform</c> is
    /// <c>v' = v * multiplier + offset</c>, and <c>Desintegr.as:62-107</c> uses each half for a
    /// different family: the four elemental tips pass <b>offsets</b> with unit multipliers (they push
    /// the body toward the effect's colour until it goes white), while the three blood tips pass
    /// <b>multipliers</b> with zero offsets (they drain two channels and leave the third, so the body
    /// goes deep red).</para>
    ///
    /// <para><b>Applied once per frame, so it compounds.</b> <c>Desintegr.step</c> calls
    /// <c>burnBmp.colorTransform(burnBmp.rect, burnCt)</c> on the <i>already transformed</i> bitmap
    /// (<c>:116</c>), which is why the blood tips' <c>0.85</c> per frame leaves the green and blue
    /// channels at <c>0.85^10 ≈ 0.20</c> after the ten-frame ramp rather than at <c>0.85</c>. Treating
    /// the multipliers as a one-shot tint would leave the body almost unchanged and is the obvious way
    /// to get this wrong.</para>
    ///
    /// <para>Values are in <b>0..1 per channel</b> — the port's colour space — not Flash's 0..255.</para>
    /// </remarks>
    public readonly struct DesintegrTint
    {
        /// <summary>Per-channel multiplier, applied every frame.</summary>
        public readonly float MulR, MulG, MulB;

        /// <summary>Per-channel offset in 0..1, added every frame.</summary>
        public readonly float AddR, AddG, AddB;

        public DesintegrTint(float mulR, float mulG, float mulB, float addR, float addG, float addB)
        {
            MulR = mulR; MulG = mulG; MulB = mulB;
            AddR = addR; AddG = addG; AddB = addB;
        }

        /// <summary>
        /// The accumulated transform after <paramref name="frames"/> applications — the value the
        /// renderer needs, since it cannot apply a transform to its own pixels.
        /// </summary>
        /// <remarks>
        /// <c>pow(mul, n)</c> for the multiplier half and <c>add * n</c> for the offset half. Neither is
        /// clamped here: Flash clamps at the byte level, and the shader clamps at output, so clamping in
        /// the middle would make the two disagree about when the body reaches full white.
        /// </remarks>
        public DesintegrTint After(int frames)
        {
            if (frames <= 0)
                return new DesintegrTint(1f, 1f, 1f, 0f, 0f, 0f);

            return new DesintegrTint(
                Pow(MulR, frames), Pow(MulG, frames), Pow(MulB, frames),
                AddR * frames, AddG * frames, AddB * frames);
        }

        static float Pow(float b, int n)
        {
            float r = 1f;
            for (int i = 0; i < n; i++) r *= b;
            return r;
        }
    }

    /// <summary>
    /// The dissolve a dead body goes through — the port of <c>fe/serv/Desintegr.as</c>, and the reason an
    /// enemy that takes far too much damage erodes into blood particles instead of vanishing.
    ///
    /// <para><b>What the oracle actually does, in one paragraph.</b> <c>Unit.initBurn(tip)</c>
    /// (<c>Unit.as:4469-4481</c>) snapshots the unit's current <c>vis</c> into a <c>BitmapData</c>,
    /// replaces the display object with that snapshot, and attaches a <c>Desintegr</c>. For ten frames
    /// the snapshot is colour-transformed toward the tip's glow colour and given a matching
    /// <c>GlowFilter</c> whose alpha ramps <c>1/10 … 10/10</c>; for the next thirty,
    /// <c>BitmapData.pixelDissolve</c> erodes it away a random block at a time while particles of the
    /// tip's own type spray out of the body's centre. When the erosion reaches 100% the unit is
    /// <c>exterminate()</c>d — out of <c>loc.units</c>, invisible, untouchable.</para>
    ///
    /// <para><b>Why the rules live here and the drawing does not.</b> Everything above that is a number —
    /// how long each phase is, what fraction has eroded, whether this frame emits, what colour the body
    /// has reached — is decided in this class, which references no engine type and can therefore be
    /// executed outside the editor. The controller owns the sprite, the material and the emitter, which
    /// is the same split <c>BloodSprayRules</c> uses for the same reason.</para>
    /// </summary>
    public static class DesintegrRules
    {
        /// <summary>
        /// <c>burnTime1</c> (<c>Desintegr.as:38</c>) — the colour-and-glow ramp, in canonical 30 Hz
        /// frames.
        /// </summary>
        public const int TintFrames = 10;

        /// <summary>
        /// <c>burnTime2</c> (<c>Desintegr.as:39</c>) — the pixel-erosion phase, in canonical frames.
        /// </summary>
        public const int ErodeFrames = 30;

        /// <summary>
        /// The frame at which the body is fully eroded — <c>burnTime1 + burnTime2</c>.
        /// </summary>
        /// <remarks>
        /// <b>The removal is one step later than this, and the difference is invisible.</b> At
        /// <c>burnN == 40</c> the erosion branch runs one last time with fraction
        /// <c>30/30 = 1.0</c> — nothing is left to draw — and <c>burnN</c> becomes 41, which is the step
        /// whose <c>vse</c> flag <c>Unit.step</c> acts on. So the port removes the unit at 40 and the
        /// oracle removes it at 41; between those two frames the oracle is drawing a fully transparent
        /// bitmap. <c>CorpseDamageMath.DissolveFrames</c> is the same 40 and is what the controller
        /// counts down, so the two constants agree by construction rather than by coincidence.
        /// </remarks>
        public const int TotalFrames = TintFrames + ErodeFrames;

        // ── The tip table ────────────────────────────────────────────────────────────────────────
        //
        // Desintegr.as:62-107. Seven presets, each a (colour transform, particle row, glow colour,
        // sound) tuple. The glow colours are the oracle's decimal literals, kept as hex beside them so
        // a reader can see the colour without converting.

        /// <summary>AS3 <c>burn</c> particles — <c>0xFFAA00</c> glow, orange fire flecks.</summary>
        public const string BurnParticleId = "burn";

        /// <summary>AS3 <c>plakap</c> particles — <c>0x00FF00</c> glow, plasma droplets.</summary>
        public const string PlasmaParticleId = "plakap";

        /// <summary>AS3 <c>krupa</c> particles — <c>0x0000FF</c> glow, ice shards.</summary>
        public const string CryoParticleId = "krupa";

        /// <summary>
        /// The red-blood particle row — AS3 <c>blood</c>.
        /// </summary>
        /// <remarks>
        /// <b>Deliberately a second constant rather than a reference to
        /// <c>BloodSprayRules.RedSprayId</c>.</b> That type lives in <c>PFE.View</c>, which references
        /// this assembly and not the other way round, so naming it here would be a circular assembly
        /// dependency. The two spellings are pinned equal by a fixture in <c>PFE.Tests</c> (which sees
        /// both), so they cannot drift silently.
        /// </remarks>
        public const string RedBloodParticleId = "blood";

        /// <summary>AS3 <c>gblood</c>. See <see cref="RedBloodParticleId"/> for why this is duplicated.</summary>
        public const string GreenBloodParticleId = "gblood";

        /// <summary>AS3 <c>pblood</c>. See <see cref="RedBloodParticleId"/> for why this is duplicated.</summary>
        public const string PinkBloodParticleId = "pblood";

        /// <summary>
        /// The tip a body overkilled past destruction dissolves with — AS3's
        /// <c>initBurn(4 + this.blood)</c> (<c>Unit.as:4347-4353</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Zero blood answers <see cref="DesintegrTip.None"/>, and that is the oracle's own
        /// branch.</b> <c>die()</c> reaches the overkill arm only under <c>this.trup &amp;&amp; this.blood
        /// &gt; 0</c>, so a bloodless body never gets here; a caller that asks anyway must get
        /// <see cref="DesintegrTip.None"/> rather than <c>tip 4</c>, which is the cryo preset and would
        /// freeze a robot's corpse.</para>
        ///
        /// <para>The three blood values are <c>1</c>, <c>2</c>, <c>3</c> and map to tips <c>5</c>,
        /// <c>6</c>, <c>7</c> — the last three rows of the table.</para>
        /// </remarks>
        public static DesintegrTip TipForOverkill(BloodType blood)
        {
            if (blood == BloodType.None)
                return DesintegrTip.None;

            return (DesintegrTip)(4 + (int)blood);
        }

        /// <summary>
        /// The tip the <b>damage type</b> asks for — AS3's <c>_loc10_</c> in <c>Unit.damage()</c>
        /// (<c>Unit.as:3713-3741</c>), which is then handed to <c>die(_loc10_)</c> and wins the branch
        /// table's <c>param1 &gt; 0</c> arm (<c>:4325</c>).
        /// </summary>
        /// <param name="type">The hit's <c>D_*</c> code — AS3 <c>param2</c>.</param>
        /// <param name="overkill">
        /// How far past zero the health pool went — the port's positive stand-in for AS3's negative
        /// <c>hp</c>. The oracle compares <c>hp &lt;= -maxhp * 3</c>, which is this value at or past
        /// three times the pool.
        /// </param>
        /// <param name="maxHp">The unit's maximum health, AS3's <c>maxhp</c>.</param>
        /// <param name="leavesCorpse">AS3 <c>trup</c> — <c>false</c> for a body that is destroyed outright.</param>
        /// <param name="rnd01">A uniform draw in <c>[0,1)</c>, AS3 <c>Math.random()</c>.</param>
        /// <remarks>
        /// <para><b>Only five of the twenty <see cref="DamageType"/> values produce a tip, and that is the
        /// oracle's table, not a subset of it.</b> Bullets, blades, physical melee, explosives, venom,
        /// EMP, spark, poison, bleed, fang, balefire, necrotic, psionic, pink and internal damage all
        /// leave <c>_loc10_</c> at <c>0</c> — an ordinary death, which for a body is a corpse and for
        /// <c>trup='0'</c> a burst. Fire and laser share tip 1, plasma and acid share tip 2, astro and
        /// friendly fire share tip 3, and cryo alone is tip 4.</para>
        ///
        /// <para><b>The fire and laser gates are not the same gate.</b> Fire needs overkill <i>or</i> a
        /// bodyless unit; laser adds a coin flip, so <b>half of all laser kills burn regardless of how
        /// little overkill there was</b>. That is <c>this.isrnd()</c> (<c>Unit.as:4855-4858</c>,
        /// <c>Math.random() &lt; 0.5</c>), and it is the reason a laser pistol leaves a dissolving body
        /// where a rifle of the same damage would leave a corpse.</para>
        ///
        /// <para><b>The draw is only taken when the first two tests fail</b>, because AS3 writes the
        /// three conditions as one short-circuiting <c>||</c>. A port that hoisted the roll above the
        /// tests would consume a random number on every fire/laser death — harmless for one unit, but it
        /// shifts the sequence every later roll in the same tick sees, so a fixture pinning the stream
        /// would drift.</para>
        ///
        /// <para><b>Sequential <c>if</c>s, not <c>else if</c>, and that is safe rather than sloppy.</b>
        /// The oracle writes five independent <c>if</c>s against a single <c>param2</c>, so at most one
        /// can fire; a <see cref="DamageType"/> is one value. The shape is kept so the table can be read
        /// beside <c>Unit.as</c> line for line.</para>
        ///
        /// <para><b>Acid and friendly fire are the two that surprise.</b> Acid burns with the
        /// <i>plasma</i> preset (tip 2) and friendly fire with the <i>astro</i> one (tip 3) — neither has
        /// a preset of its own, and inventing one would be inventing art.</para>
        /// </remarks>
        public static DesintegrTip TipForDamageType(
            DamageType type, float overkill, float maxHp, bool leavesCorpse, float rnd01)
        {
            // AS3 `this.hp <= -this.maxhp * 3`. The port never drives `hp` negative — it accumulates a
            // positive `Overkill` — so the same threshold is read from the other side.
            bool destroyedThreeTimesOver = overkill >= maxHp * 3f;

            DesintegrTip tip = DesintegrTip.None;

            if (type == DamageType.Fire && (destroyedThreeTimesOver || !leavesCorpse))
            {
                tip = DesintegrTip.Burn;
            }

            // The roll is the LAST operand on purpose — see the remarks.
            if (type == DamageType.Laser
                && (destroyedThreeTimesOver || !leavesCorpse || rnd01 < 0.5f))
            {
                tip = DesintegrTip.Burn;
            }

            if (type == DamageType.Plasma || type == DamageType.Acid)
            {
                tip = DesintegrTip.Plasma;
            }

            if (type == DamageType.Astral || type == DamageType.FriendlyFire)
            {
                tip = DesintegrTip.Astro;
            }

            if (type == DamageType.Cryo)
            {
                tip = DesintegrTip.Cryo;
            }

            return tip;
        }

        /// <summary>Whether this tip describes a dissolve at all (i.e. is one of the seven presets).</summary>
        public static bool IsDissolve(DesintegrTip tip)
            => tip >= DesintegrTip.Burn && tip <= DesintegrTip.BloodPink;

        /// <summary>
        /// The particle row the erosion sprays — <c>burnPart</c> (<c>Desintegr.as:65-107</c>).
        /// <c>null</c> for <see cref="DesintegrTip.None"/>.
        /// </summary>
        /// <remarks>
        /// <b>Tip 3 reuses <c>burn</c>.</b> Only the glow colour and the RGB ramp differ between the
        /// fire and the astro presets — the particles are the same row. A port that invented an
        /// <c>astro</c> particle would be inventing art.
        /// </remarks>
        public static string ParticleId(DesintegrTip tip)
        {
            switch (tip)
            {
                case DesintegrTip.Burn:       return BurnParticleId;
                case DesintegrTip.Plasma:     return PlasmaParticleId;
                case DesintegrTip.Astro:      return BurnParticleId;
                case DesintegrTip.Cryo:       return CryoParticleId;
                case DesintegrTip.BloodRed:   return RedBloodParticleId;
                case DesintegrTip.BloodGreen: return GreenBloodParticleId;
                case DesintegrTip.BloodPink:  return PinkBloodParticleId;
                default:                      return null;
            }
        }

        /// <summary>
        /// The one-shot sound played when the dissolve starts — <c>Snd.ps(id, X, Y)</c>
        /// (<c>Desintegr.as:67, :74, :81, :88</c>). <c>null</c> for the three blood tips.
        /// </summary>
        /// <remarks>
        /// <b>The blood tips are silent, and that is not an omission.</b> Their constructor blocks
        /// (<c>:90-107</c>) contain no <c>Snd.ps</c> call at all — a body that was already shredded
        /// does not hiss on its way out. The sound the player hears at that moment is the death rattle
        /// from <c>die()</c>'s own head, which is a different call site.
        /// </remarks>
        public static string SoundId(DesintegrTip tip)
        {
            switch (tip)
            {
                case DesintegrTip.Burn:   return "desintegr_f";
                case DesintegrTip.Plasma: return "liquid_f";
                case DesintegrTip.Astro:  return "desintegr_f";
                case DesintegrTip.Cryo:   return "freezing_f";
                default:                  return null;
            }
        }

        /// <summary>
        /// The glow colour as a packed <c>0xRRGGBB</c> integer — the literal
        /// <c>GlowFilter(colour, …)</c> is constructed with (<c>Desintegr.as:66-106</c>).
        /// </summary>
        /// <remarks>
        /// Kept as an int rather than a colour object so this class stays engine-free; the presentation
        /// side splits it. <c>0</c> for <see cref="DesintegrTip.None"/>.
        /// </remarks>
        public static int GlowColorRgb(DesintegrTip tip)
        {
            switch (tip)
            {
                case DesintegrTip.Burn:       return 16755200;   // 0xFFAA00
                case DesintegrTip.Plasma:     return 65280;      // 0x00FF00
                case DesintegrTip.Astro:      return 4474111;    // 0x4444FF
                case DesintegrTip.Cryo:       return 255;        // 0x0000FF
                case DesintegrTip.BloodRed:   return 16711680;   // 0xFF0000
                case DesintegrTip.BloodGreen: return 6736947;    // 0x66CC33
                case DesintegrTip.BloodPink:  return 16738047;   // 0xFF66FF
                default:                      return 0;
            }
        }

        /// <summary>
        /// The per-frame colour transform for a tip (<c>Desintegr.as:62-107</c>), before any frame is
        /// applied. See <see cref="DesintegrTint"/> for why it compounds.
        /// </summary>
        /// <remarks>
        /// <para><b>The four elemental tips push toward a colour, the three blood tips drain toward
        /// one.</b> Tip 1 is <c>(1,1,1,+25.5,+10,0)</c> — red and green climb while blue stays, which is
        /// orange, and after ten frames the body is <c>+255/+100</c>, i.e. saturated. Tip 5 is
        /// <c>(1,0.85,0.85,0,0,0)</c> — a pure drain of two channels.</para>
        ///
        /// <para>The offsets are the oracle's <c>255/10</c>-style expressions divided by 255 to reach the
        /// port's 0..1 colour space: <c>255/10/255 = 0.1</c>. Writing them as <c>255/10</c> here would
        /// apply a hundredfold tint on the first frame.</para>
        /// </remarks>
        public static DesintegrTint TintFor(DesintegrTip tip)
        {
            const float f = 1f / TintFrames;

            switch (tip)
            {
                // (1,1,1) with +255/+100/0 over ten frames.
                case DesintegrTip.Burn:   return new DesintegrTint(1f, 1f, 1f, 255f * f / 255f, 100f * f / 255f, 0f);

                // (1,1,1) with 0/+255/+100 — the green-to-yellow plasma ramp.
                case DesintegrTip.Plasma: return new DesintegrTint(1f, 1f, 1f, 0f, 255f * f / 255f, 100f * f / 255f);

                // (1,1,1) with +155/+155/+255 — blue-white.
                case DesintegrTip.Astro:  return new DesintegrTint(1f, 1f, 1f, 155f * f / 255f, 155f * f / 255f, 255f * f / 255f);

                // (1,1,1) with +100/+100/+255.
                case DesintegrTip.Cryo:   return new DesintegrTint(1f, 1f, 1f, 100f * f / 255f, 100f * f / 255f, 255f * f / 255f);

                // (1,0.85,0.85) — red survives, green and blue drain.
                case DesintegrTip.BloodRed:   return new DesintegrTint(1f, 0.85f, 0.85f, 0f, 0f, 0f);

                // (0.9,1,0.85) — the zombie green.
                case DesintegrTip.BloodGreen: return new DesintegrTint(0.9f, 1f, 0.85f, 0f, 0f, 0f);

                // (1,0.85,0.88) — the pink.
                case DesintegrTip.BloodPink:  return new DesintegrTint(1f, 0.85f, 0.88f, 0f, 0f, 0f);

                default: return new DesintegrTint(1f, 1f, 1f, 0f, 0f, 0f);
            }
        }

        /// <summary>
        /// The tint after <paramref name="frame"/> frames of the ramp — <c>frame</c> is
        /// <b>1-based</b>, matching <c>burnN</c>, which the constructor seeds at <c>1</c>
        /// (<c>Desintegr.as:109</c>).
        /// </summary>
        /// <remarks>
        /// Clamped to <see cref="TintFrames"/>: the oracle stops transforming once
        /// <c>burnN &gt; burnTime1</c> (<c>:114-119</c>), so frame 11 and frame 40 hold the same colour.
        /// A caller passing 0 gets the identity, which is the state before the first step.
        /// </remarks>
        public static DesintegrTint TintAt(DesintegrTip tip, int frame)
        {
            if (frame <= 0)
                return new DesintegrTint(1f, 1f, 1f, 0f, 0f, 0f);

            return TintFor(tip).After(frame < TintFrames ? frame : TintFrames);
        }

        /// <summary>
        /// How much of the body has been eaten away by <paramref name="frame"/>, in <c>0..1</c> — AS3's
        /// <c>burnKolPix * (burnN - burnTime1) / burnTime2</c> (<c>Desintegr.as:121</c>) with the pixel
        /// count factored out, since the shader works in UV rather than in pixels.
        /// </summary>
        /// <remarks>
        /// <para><b>Zero through the whole tint phase.</b> The erosion branch does not run until
        /// <c>burnN &gt; burnTime1</c>, so a caller that used this value as a threshold from frame 1
        /// would start eating the body while the oracle is still colouring it.</para>
        ///
        /// <para>Reaches exactly <c>1.0</c> at <see cref="TotalFrames"/> and stays there.</para>
        /// </remarks>
        public static float ErodeFraction(int frame)
        {
            if (frame <= TintFrames) return 0f;
            if (frame >= TotalFrames) return 1f;

            return (frame - TintFrames) / (float)ErodeFrames;
        }

        /// <summary>
        /// The glow's alpha at <paramref name="frame"/> — <c>burnN / burnTime1</c>
        /// (<c>Desintegr.as:117</c>), clamped to <c>1</c>.
        /// </summary>
        /// <remarks>
        /// <b>It ramps during the tint phase and then stays at full.</b> The oracle only <i>assigns</i>
        /// the filter inside the first branch, but a filter it has already assigned keeps its last
        /// value, so the halo does not fade out when the erosion starts. Answering <c>0</c> for frames
        /// past 10 would be the plausible-looking wrong reading.
        /// </remarks>
        public static float GlowAlpha(int frame)
        {
            if (frame <= 0) return 0f;
            if (frame >= TintFrames) return 1f;

            return frame / (float)TintFrames;
        }

        /// <summary>
        /// Whether the erosion has finished and the body should be removed — AS3's <c>burn.vse</c>
        /// (<c>Desintegr.as:130-133</c>), read by <c>Unit.step</c> (<c>Unit.as:1839-1843</c>).
        /// </summary>
        public static bool IsComplete(int frame) => frame >= TotalFrames;

        /// <summary>
        /// Whether this frame sprays particles — the erosion phase only, <c>burnTime1 &lt; burnN &lt;=
        /// burnTime1 + burnTime2</c> (<c>Desintegr.as:119</c>).
        /// </summary>
        public static bool EmitsParticles(int frame) => frame > TintFrames && frame <= TotalFrames;

        /// <summary>
        /// The per-frame particle gate — <c>massa &gt;= 0.25 || Math.random() &lt; massa * 4</c>
        /// (<c>Desintegr.as:122</c>).
        /// </summary>
        /// <param name="mass">AS3 <c>massa</c>, already divided by 50 (<c>IDamageable.Mass</c>).</param>
        /// <param name="rnd01">One draw from the presentation stream, or a constant for an offline host.</param>
        /// <remarks>
        /// <para><b>A light body emits most of the time anyway, and that is the rule's point.</b> The
        /// disjunction exists so that a heavy body (which always passes the first term) emits every
        /// frame while a light one emits with probability <c>4 * massa</c> — for <c>massa = 0.1</c>, 40%
        /// of frames. A port that emitted unconditionally would be right for every unit above 0.25 and
        /// silently denser for the rats.</para>
        ///
        /// <para>The draw is consumed <b>only when the first term fails</b>, exactly as AS3's
        /// <c>||</c> short-circuits — which is why this takes the random value as an argument rather
        /// than an RNG.</para>
        /// </remarks>
        public static bool ShouldEmit(float mass, float rnd01)
            => mass >= 0.25f || rnd01 < mass * 4f;

        /// <summary>
        /// How many particles <c>Unit.expl()</c> throws when a body is removed without a dissolve —
        /// <c>{kol: massa * 50, rx: scX/2, ry: scY/2}</c> (<c>Unit.as:4442-4446</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Truncated, not rounded.</b> AS3 passes the product to <c>Emitter.cast</c>, which
        /// loops <c>while(i &lt; param4.kol)</c> over an integer counter (<c>Emitter.as:171-179</c>), so
        /// <c>0.7 * 50 = 35</c> spawns 35 and <c>0.49 * 50 = 24.5</c> spawns 24. Rounding would add a
        /// particle to every fractional body.</para>
        ///
        /// <para>Capped at 50, which is <c>Emitter.cast</c>'s own cap (<c>Emitter.as:176-179</c>) — a
        /// boss with <c>massa = 1.5</c> asks for 75 and gets 50.</para>
        /// </remarks>
        public static int ExplosionCount(float mass)
        {
            if (mass <= 0f) return 0;

            float raw = mass * 50f;
            int count = (int)raw;

            return count > MaxCastParticles ? MaxCastParticles : count;
        }

        /// <summary>
        /// <c>Emitter.cast</c>'s per-call particle cap (<c>Emitter.as:176-179</c>).
        /// </summary>
        public const int MaxCastParticles = 50;

        /// <summary>
        /// The X jitter radius of one dissolve particle, in AS3 pixels — <b>and it is <c>scY * 0.5</c>,
        /// not <c>scX * 0.75</c>.</b>
        /// </summary>
        /// <remarks>
        /// <para><b>The oracle's object literal repeats the key, and the second value wins.</b>
        /// <c>Desintegr.as:124-127</c> reads:</para>
        /// <code>
        /// Emitter.emit(this.burnPart, loc, X, Y - scY/2, { "rx": scX * 0.75, "rx": scY * 0.5 });
        /// </code>
        /// <para>— <c>rx</c> twice, so <c>ry</c> is never supplied at all and <c>rx</c> ends up as
        /// <c>scY * 0.5</c>. It reads as a typo in the shipped game, and it is the behaviour: the spray
        /// is a vertical-band jitter with no horizontal band, which is also why it looks like the body is
        /// crumbling rather than exploding. Reproducing the "intended" <c>rx = scX*0.75, ry = scY*0.5</c>
        /// would change the shape of every dissolve in the game.</para>
        /// </remarks>
        public static float ParticleJitterX(float spriteWidthPixels, float spriteHeightPixels)
            => spriteHeightPixels * 0.5f;

        /// <summary>
        /// The Y jitter radius — <b>always zero</b>, because the oracle's duplicated key leaves
        /// <c>ry</c> unset. See <see cref="ParticleJitterX"/>.
        /// </summary>
        public static float ParticleJitterY(float spriteWidthPixels, float spriteHeightPixels) => 0f;

        /// <summary>
        /// The <c>GlowFilter</c>'s blur radius, in AS3 pixels — <c>blurX = blurY = 3</c>
        /// (<c>Desintegr.as:117</c>), with <c>strength = 2</c> and <c>quality = 3</c>.
        /// </summary>
        /// <remarks>
        /// Exposed because the port's glow is a shader rim rather than a real blur: the radius has to be
        /// turned into a UV offset from the sprite's own texel size, and that conversion belongs with the
        /// number it converts.
        /// </remarks>
        public const float GlowBlurPixels = 3f;
    }
}
