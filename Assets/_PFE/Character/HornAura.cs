using UnityEngine;

namespace PFE.Character
{
    /// <summary>
    /// The glow that surrounds the unicorn horn while magic is charged — symbol 239, the
    /// <c>morda</c> child instance named <c>magic</c>, which is the sibling of <c>horn</c> (symbol
    /// 237) at the <i>identical</i> position, rotation and scale in all 881 placements of
    /// <c>PlayerAnimationDefinition</c>. SWF depth 23 against the horn's 21, so it draws in front.
    ///
    /// <para><b>What the art is.</b> Not a disc: a 53x59 outline of the horn plus a soft halo,
    /// measured against the shared origin it is a ~2x scaled copy of the horn's own 25x33 ink box,
    /// centred on it to within 1.7 px (<c>aura_proof.py</c> §4, <c>aura_on_horn.png</c>). The disc in
    /// the appearance preview is a different object — see "what is not here" below.</para>
    ///
    /// <para><b>Why the rule lives here and not in a renderer.</b> The glow is invisible until the
    /// game asks for it, and "asks for it" is a ramp, not a switch. AS3 keeps two integers — a target
    /// <c>aMagic</c> (0 / 50 / 100) and a current <c>aMC</c> that walks toward the target by 5 per
    /// tick — then writes <c>alpha = aMC / 100</c> and <c>visible = alpha &gt; 0</c>
    /// (<c>UnitPlayer.as:4851-4852</c>). Putting that arithmetic inside
    /// <see cref="CharacterSpriteAssembler"/> would make a renderer the owner of a simulation value;
    /// this way the assembler is told an alpha and the simulation decides it, which is also what lets
    /// the A/B window drive the same number by hand.</para>
    ///
    /// <para><b>The targets, in the original's own terms.</b>
    /// <c>aMagic = 50</c> while a weapon whose <c>vis</c> is visible is drawn and it is not a
    /// throwable (<c>:1142-1150</c>); <c>aMagic = 100</c> while levitating (<c>:1284</c>) or while a
    /// telekinesis target of mass ≥ 1 is held (<c>:1234-1238</c>); <c>0</c> otherwise, forced to
    /// <c>0</c> when <c>sost &gt; 1</c> (<c>:1286-1289</c>). The glow therefore says "magic is
    /// charged" — it is not permanent decoration, which is why the importer's "invisible by default"
    /// note was half right: the art exists, the alpha does not.</para>
    ///
    /// <para><b>What is deliberately not here: the spin.</b> <c>Displ.as:131</c> reads
    /// <c>mm.horn.magic.krug.rotation = 90 + t_anim * 0.67</c>, and it is tempting to apply it to this
    /// part. It would be wrong twice over. First, symbol 239's display list has exactly one child — an
    /// unnamed shape (238) — so there is no instance named <c>krug</c> inside it and the AS3 path
    /// cannot be addressing this object. Second, the art is horn-shaped, so spinning it about the
    /// origin would swing the glow off the horn rather than animate anything. <c>mm</c> is the
    /// appearance-preview composite, which has its own <c>horn</c> holding a <c>krug</c> circle; that
    /// object has no sprite in this port and no counterpart in the in-game path.</para>
    /// </summary>
    public static class HornAura
    {
        /// <summary>The part this drives. Symbol 239 in <c>PlayerAnimationDefinition</c>.</summary>
        public const string PartName = "magic";

        /// <summary><c>UnitPlayer.as:1480/1484</c> — <c>aMC += 5</c> / <c>aMC -= 5</c> per tick.</summary>
        public const float RampPerTick = 5f;

        /// <summary>No weapon drawn, not levitating, not holding anything: the glow is off.</summary>
        public const float LevelIdle = 0f;

        /// <summary>A magic weapon is drawn (<c>UnitPlayer.as:1144</c>).</summary>
        public const float LevelWeaponDrawn = 50f;

        /// <summary>Levitation, or a telekinesis target of mass ≥ 1 (<c>:1237</c>, <c>:1284</c>).</summary>
        public const float LevelLevitate = 100f;

        /// <summary><c>aMC</c> is an int the original compares against 100, so 100 is the ceiling.</summary>
        public const float LevelMax = 100f;

        /// <summary>
        /// One tick of the ramp: ±<see cref="RampPerTick"/> toward <paramref name="target"/>, stopping
        /// on it rather than stepping past. The original clamps only the target (0/50/100 are the only
        /// values assigned) and lets the current value overshoot by at most 4, which is invisible in
        /// alpha — this version does not overshoot, because reproducing an overshoot the eye cannot see
        /// buys nothing and hides the intent.
        /// </summary>
        public static float StepLevel(float current, float target)
        {
            target = Mathf.Clamp(target, LevelIdle, LevelMax);
            if (current < target)
            {
                return Mathf.Min(current + RampPerTick, target);
            }

            if (current > target)
            {
                return Mathf.Max(current - RampPerTick, target);
            }

            return current;
        }

        /// <summary><c>aMC / 100</c>, clamped — the number a renderer multiplies into the part's alpha.</summary>
        public static float Normalized(float level) => Mathf.Clamp01(level / LevelMax);
    }
}
