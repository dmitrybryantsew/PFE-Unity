using UnityEngine;

namespace PFE.Sim.Radiation
{
    /// <summary>
    /// The arithmetic of the player's radiation exposure — AS3 <c>UnitPlayer.actions()</c>'s radiation
    /// block (<c>UnitPlayer.as:898-960</c>), <c>heal()</c>'s radiation arm (<c>:3151-3210</c>),
    /// <c>raddamage()</c> (<c>:3227-3249</c>) and <c>resurect()</c>'s clamp (<c>:3456-3468</c>).
    ///
    /// <para><b>Why this is a pure static class.</b> The model is four numbers and a fixed order of
    /// operations, and every one of them is a claim about the oracle. Keeping them out of the
    /// <c>MonoBehaviour</c> that gathers them means a test can pin the order without a scene, a
    /// <c>SimLoop</c> or a player prefab — the same split <c>CharacterStats.TickMana</c> uses for mana.
    /// </para>
    ///
    /// <para><b>The unit of <c>drad</c> is "hp of exposure per frame".</b> It is not a rate in seconds:
    /// AS3 adds it up every frame and converts once with <c>/ 30</c>, so a source authored as
    /// <c>rad="3"</c> on a room means <b>3 hp of radiation capacity per second</b>, applied in
    /// 0.1-hp steps at 30 Hz.</para>
    /// </summary>
    public static class RadiationMath
    {
        /// <summary>
        /// <c>UnitPlayer.as:922</c> — <c>this.rad += this.drad / 30 * this.radX</c>. AS3 runs at a fixed
        /// 30 fps, so the divisor is the frame rate, not a tuning knob.
        /// </summary>
        public const float FramesPerSecond = 30f;

        /// <summary>
        /// <c>UnitPlayer.as:915</c> — a hit's radiation (<c>drad2</c>) bleeds off by this much per frame,
        /// so a single <c>raddamage=6</c> hit keeps adding for 60 frames.
        /// </summary>
        public const float HitDecayPerFrame = 0.1f;

        /// <summary>
        /// <c>UnitPlayer.as:946-949</c> — when nothing is being added the oracle parks <c>drad</c> at
        /// <c>-0.0001</c> rather than 0, so the "call <c>gui.setHp()</c> once" branch runs on the frame
        /// exposure <i>stops</i> and not on every idle frame afterwards. The value is arbitrary and only
        /// its sign matters: it must be non-zero (so <c>drad == 0</c> is false) and non-positive (so the
        /// <c>drad &gt; 0</c> branch is skipped).
        /// </summary>
        public const float IdleSentinel = -0.0001f;

        /// <summary><c>UnitPlayer.as:936</c> — <c>Math.min(0.5, drad / 10)</c>: the geiger click chance.</summary>
        public const float GeigerChanceCap = 0.5f;

        /// <summary><c>UnitPlayer.as:936</c> — the divisor in <c>drad / 10</c>.</summary>
        public const float GeigerChanceDivisor = 10f;

        /// <summary><c>UnitPlayer.as:937</c> — below this much exposure in a frame the geiger is silent.</summary>
        public const float GeigerThreshold = 0.1f;

        /// <summary><c>UnitPlayer.as:943</c> — <c>this.pet.heal(this.drad / 15, 1)</c>.</summary>
        public const float PetHealDivisor = 15f;

        /// <summary>
        /// <c>UnitPlayer.as:3239-3246</c> — the step in <c>raddamage</c>'s non-zero <c>radtip</c> arm:
        /// inside the outer quarter of the radius the damage scales linearly, and from a quarter inward
        /// it is flat at full strength. A <b>cliff</b>, not a falloff — see <see cref="ElementalShare"/>.
        /// </summary>
        public const float ElementalStep = 0.25f;

        /// <summary><c>UnitPlayer.as:3241</c> — <c>param1 *= 4</c> in that same arm.</summary>
        public const float ElementalStepScale = 4f;

        /// <summary>
        /// <c>UnitPlayer.as:3461-3463</c> — resurrection never leaves the player with more than
        /// <c>maxhp - 40</c> of radiation, i.e. it always leaves 40 hp of usable health.
        /// </summary>
        public const float ResurrectHeadroom = 40f;

        /// <summary>
        /// <c>Obj.as:38</c> — the default <c>radrad</c> radius for a radioactive object that does not
        /// author one: 250 Flash px.
        /// </summary>
        public const float DefaultSourceRadius = 250f;

        /// <summary>
        /// One frame of the player's radiation block.
        ///
        /// <para>The order is the oracle's and it matters: ambient exposure is added <i>before</i> the
        /// hit term, the perk cap is applied <i>before</i> the hp clamp, and the hp clamp is what can
        /// kill. Moving any one of them changes a death.</para>
        /// </summary>
        public struct TickInput
        {
            /// <summary><c>gg.maxhp</c>.</summary>
            public float MaxHp;

            /// <summary><c>gg.hp</c> — the current health, which the clamp may lower.</summary>
            public float Hp;

            /// <summary><c>gg.rad</c> — exposure already accumulated, in hp.</summary>
            public float Rad;

            /// <summary><c>gg.drad</c> — exposure added this frame by ambient sources.</summary>
            public float Drad;

            /// <summary><c>gg.drad2</c> — the decaying remainder of a hit's radiation.</summary>
            public float Drad2;

            /// <summary><c>gg.radX</c> — the multiplier from perks and armour. <c>0</c> = immune.</summary>
            public float RadX;

            /// <summary><c>gg.pers.radChild</c> — when positive, caps exposure at <c>maxhp*(1-this)</c>.</summary>
            public float RadChild;

            /// <summary><c>loc.rad</c> — the current room's ambient level (0 when there is no room).</summary>
            public float AmbientRad;

            /// <summary><c>loc.wrad</c> — the current room's water level, added only while submerged.</summary>
            public float AmbientWaterRad;

            /// <summary><c>UnitPlayer.inWater</c>.</summary>
            public bool InWater;

            /// <summary>
            /// <c>UnitPlayer.noRad</c> — a one-frame grace so the frame the player lands in a new room
            /// does not bill them for it. AS3 consumes it (<c>:901-904</c>), so the caller must clear it.
            /// </summary>
            public bool NoRad;

            /// <summary><c>UnitPlayer.invulner</c> — a scripted invulnerability also stops absorption.</summary>
            public bool Invulnerable;

            /// <summary><c>World.w.godMode</c>.</summary>
            public bool GodMode;
        }

        /// <summary>What one frame did.</summary>
        public struct TickResult
        {
            /// <summary><c>gg.rad</c> after the frame.</summary>
            public float Rad;

            /// <summary><c>gg.drad</c> after the frame — 0, or the idle sentinel.</summary>
            public float Drad;

            /// <summary><c>gg.drad2</c> after the frame.</summary>
            public float Drad2;

            /// <summary><c>gg.hp</c> after the clamp. Equal to the input when nothing clamped.</summary>
            public float Hp;

            /// <summary>Whether the oracle would have called <c>gui.setHp()</c> this frame.</summary>
            public bool HpDirty;

            /// <summary><c>hp &lt;= 0</c> after the clamp — <c>this.die()</c> (<c>:929-931</c>).</summary>
            public bool Died;

            /// <summary><c>min(0.5, drad/10)</c>, or 0 when the geiger is silent. Compare with a roll.</summary>
            public float GeigerChance;

            /// <summary>
            /// <c>drad &gt; 0.1</c> (<c>UnitPlayer.as:937</c>) — the gate that stops a trace amount of
            /// exposure clicking. The caller rolls <see cref="GeigerChance"/> only when this is true.
            /// </summary>
            public bool GeigerEligible;

            /// <summary>
            /// <c>drad / 15</c> (<c>UnitPlayer.as:943</c>) — the amount an active pet is healed with
            /// <c>heal(amount, 1)</c>, i.e. queued rather than applied instantly. Zero when no exposure
            /// was added, which is also when the oracle skips the call.
            /// </summary>
            public float PetHeal;
        }

        /// <summary>
        /// Runs <c>UnitPlayer.as:898-960</c> for one frame.
        /// </summary>
        public static TickResult Tick(in TickInput input)
        {
            float drad = input.Drad;
            float drad2 = input.Drad2;
            float rad = input.Rad;
            float hp = input.Hp;

            var result = default(TickResult);

            // :901-911 — `if(this.noRad) { this.noRad = false; } else { drad += loc.rad; if(inWater) ... }`
            if (!input.NoRad)
            {
                drad += input.AmbientRad;
                if (input.InWater)
                {
                    drad += input.AmbientWaterRad;
                }
            }

            // :913-917 — the hit term decays by 0.1 per frame while it is positive.
            if (drad2 > 0f)
            {
                drad += drad2;
                drad2 -= HitDecayPerFrame;
            }

            if (drad > 0f)
            {
                // :920-933 — absorption, perk cap, hp clamp, death. `radX > 0` is the "not immune"
                // gate: a rad suit at 0 makes the whole block a no-op, including the geiger.
                if (input.RadX > 0f && !input.Invulnerable && !input.GodMode)
                {
                    rad += drad / FramesPerSecond * input.RadX;
                    rad = CapForPerk(rad, input.MaxHp, input.RadChild);

                    if (hp > input.MaxHp - rad)
                    {
                        hp = input.MaxHp - rad;
                        result.HpDirty = true;
                        if (hp <= 0f)
                        {
                            result.Died = true;
                        }
                    }
                }

                // :935-943 — the geiger click, and the pet's share. Both read the frame's total
                // exposure, which is why they are computed before `drad` is consumed.
                result.GeigerChance = Mathf.Min(GeigerChanceCap, drad / GeigerChanceDivisor);
                result.GeigerEligible = drad > GeigerThreshold;
                result.PetHeal = drad / PetHealDivisor;

                // :945 — consumed.
                drad = 0f;
            }
            else if (drad == 0f)
            {
                // :948-951 — the "exposure stopped" frame: refresh the bar once, then park the
                // sentinel so the branch cannot run again until something adds exposure.
                result.HpDirty = true;
                drad = IdleSentinel;
            }

            result.Rad = rad;
            result.Drad = drad;
            result.Drad2 = drad2;
            result.Hp = hp;
            return result;
        }

        /// <summary>
        /// <c>UnitPlayer.as:923-926</c> — the Rad Child perk caps exposure at
        /// <c>maxhp * (1 - radChild)</c>. The oracle only caps when <c>radChild &gt; 0</c>; with the perk
        /// absent the cap is <c>maxhp</c> (the clamp is simply not applied), which is why this returns
        /// <paramref name="rad"/> untouched in that case rather than clamping to <c>maxhp</c>.
        /// </summary>
        public static float CapForPerk(float rad, float maxHp, float radChild)
        {
            if (radChild <= 0f)
            {
                return rad;
            }

            float cap = maxHp * (1f - radChild);
            return rad > cap ? cap : rad;
        }

        /// <summary>
        /// The health ceiling radiation imposes — <c>maxhp - rad</c>. AS3 writes this clamp in three
        /// places (<c>UnitPlayer.as:927</c> the tick, <c>:3157</c> a direct heal, <c>:963</c> regen), and
        /// they are the same expression, so they share one function.
        /// </summary>
        public static float HpCeiling(float maxHp, float rad)
        {
            float ceiling = maxHp - rad;
            return ceiling > 0f ? ceiling : 0f;
        }

        /// <summary>
        /// <c>heal(amount, 2)</c> — <c>UnitPlayer.as:3173-3178</c>: radiation removed, floored at 0.
        /// Used by RadAway (<c>Invent.as:430-433</c>) and by the clinic's radiation row
        /// (<c>PipPageMed.as:298-305</c>).
        /// </summary>
        public static float RadAfterRemoval(float rad, float amount)
        {
            float after = rad - amount;
            return after > 0f ? after : 0f;
        }

        /// <summary>
        /// <c>UnitPlayer.as:3461-3467</c> — resurrection clamps exposure to <c>maxhp - 40</c> and floors
        /// it at 0. The headroom exists so a resurrection is never immediately undone by the hp clamp.
        /// </summary>
        public static float RadAfterResurrect(float rad, float maxHp)
        {
            float capped = rad > maxHp - ResurrectHeadroom ? maxHp - ResurrectHeadroom : rad;
            return capped > 0f ? capped : 0f;
        }

        /// <summary>
        /// <c>Obj.as:231-236</c> — the normalised strength of a radioactive source at a distance:
        /// <c>(radrad - dist) / radrad</c>, i.e. 1 at the centre and 0 at the rim, <b>linear</b>.
        ///
        /// <para>Returns 0 outside the radius, which is the oracle's own gate
        /// (<c>this.rasst2 &lt; this.radrad * this.radrad</c>) rather than a separate check at the call
        /// site.</para>
        /// </summary>
        public static float Falloff(float distance, float radius)
        {
            if (radius <= 0f || distance >= radius)
            {
                return 0f;
            }

            float normalised = (radius - distance) / radius;
            return normalised > 0f ? normalised : 0f;
        }

        /// <summary>
        /// <c>UnitPlayer.as:3239-3246</c> — the non-radiation arm of <c>raddamage</c>.
        ///
        /// <para><b>This is a cliff, not a falloff.</b> Outside the inner 75 % of the radius the
        /// contribution is <c>normalised * 4</c>; anywhere inside it, the contribution is <b>full
        /// strength</b>. So a gas cloud deals the same venom damage at 10 % of its radius and at 74 %,
        /// then drops off sharply. Reproducing it as a linear ramp would make the inner 75 % far weaker
        /// than the oracle.</para>
        /// </summary>
        public static float ElementalShare(float normalised)
        {
            if (normalised < ElementalStep)
            {
                return normalised * ElementalStepScale;
            }

            return 1f;
        }

        /// <summary>
        /// Whether a source at this offset is inside its radius — the squared-distance test from
        /// <c>Obj.ggModum</c> (<c>Obj.as:233</c>), kept in one place so the caller never squares twice.
        /// </summary>
        public static bool InRadius(float dx, float dy, float radius)
        {
            if (radius <= 0f)
            {
                return false;
            }

            return dx * dx + dy * dy < radius * radius;
        }

        /// <summary>
        /// The vertical offset <c>Obj.getRasst2</c> measures for the player — <c>Obj.as:203-208</c>:
        /// <c>dy = gg.Y - gg.scY * 0.75 - obj.Y + obj.scY / 2</c>.
        ///
        /// <para>Three quarters of the <i>player's</i> height up from their feet, measured against the
        /// middle of the object. It is not "centre to centre": the player's chest is the sample point,
        /// which is why a barrel at their feet still reads as close.</para>
        /// </summary>
        public static float VerticalOffset(float playerFeetY, float playerHeight, float objectCentreY)
        {
            return playerFeetY - playerHeight * 0.75f - objectCentreY;
        }
    }
}
