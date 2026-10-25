using UnityEngine;
using PFE.Sim.Radiation;

namespace PFE.Entities.Player
{
    /// <summary>
    /// The player's live radiation state — AS3's <c>UnitPlayer.rad</c>, <c>drad</c>, <c>drad2</c>,
    /// <c>noRad</c>, <c>t_nogas</c>, <c>t_raddam</c> and <c>ddam1..3</c> (<c>UnitPlayer.as:127-135</c>,
    /// <c>:219-223</c>, <c>:101</c>).
    ///
    /// <para><b>Why these are not fields on <see cref="PFE.Systems.RPG.CharacterStats"/>.</b>
    /// <c>CharacterStats</c> is the port of <c>Pers</c> — the <i>perk and skill</i> table. In AS3
    /// <c>rad</c> lives on the unit and the perks live on <c>Pers</c>; <c>pers.radChild</c> is read by
    /// the unit, not the other way round. Keeping the split means a radiation value cannot be reset by
    /// a stat recompute, which is what would happen if it sat in <c>ResetToDefaults</c>'s blast radius.
    /// </para>
    ///
    /// <para><b>Not a <c>MonoBehaviour</c>.</b> Nothing here needs a scene object, and a plain class is
    /// what lets <see cref="PFE.Tests"/> drive a hundred frames without a player prefab.</para>
    /// </summary>
    public sealed class PlayerRadiationState
    {
        /// <summary>
        /// <c>UnitPlayer.rad</c> — accumulated exposure in <b>hp</b>. The hp ceiling is
        /// <c>maxhp - rad</c>, so this number is literally health the player has lost to radiation and
        /// cannot heal back until it is cured.
        /// </summary>
        public float Rad { get; private set; }

        /// <summary><c>UnitPlayer.drad</c> — exposure gathered this frame, in hp per frame.</summary>
        public float Drad { get; private set; }

        /// <summary>
        /// <c>UnitPlayer.drad2</c> — a hit's radiation, still bleeding in. Assigned by
        /// <see cref="AddHitRadiation"/> (the oracle assigns rather than adds, so two hits in one frame
        /// keep only the last) and decayed by <see cref="RadiationMath.HitDecayPerFrame"/> per frame.
        /// </summary>
        public float Drad2 { get; private set; }

        /// <summary>
        /// <c>UnitPlayer.noRad</c> — starts <b>true</b> (<c>UnitPlayer.as:101</c>), so the player's first
        /// frame after a spawn does not bill them for the room they spawned in. Consumed by the first
        /// <see cref="Tick"/>.
        /// </summary>
        public bool NoRad { get; private set; } = true;

        /// <summary>
        /// <c>UnitPlayer.t_nogas</c> — frames during which <c>raddamage()</c> is ignored entirely
        /// (<c>:3229-3231</c>). Set to <b>90</b> on every location change (<c>:654</c>) and <b>250</b> on
        /// resurrection (<c>:3481</c>).
        ///
        /// <para>It gates <b>sources</b> (objects, auras, hits), not the room's ambient <c>loc.rad</c>:
        /// ambient is added in <c>actions()</c>, which never consults this counter.</para>
        /// </summary>
        public int NoGasFrames { get; private set; }

        /// <summary>
        /// <c>UnitPlayer.ddam1..3</c> — elemental damage from a source whose <c>radtip</c> is not 0,
        /// accumulated per frame and paid out every 30 frames (<c>:1534-1551</c>). Index 0 is unused
        /// because the oracle's attribute values are 1-based.
        /// </summary>
        private readonly float[] _elemental = new float[4];

        /// <summary><c>UnitPlayer.t_raddam</c> — the frame counter for that payout.</summary>
        private int _elementalClock;

        /// <summary>
        /// The chance the geiger clicked last tick, already capped by the oracle at 0.5. Exposed so the
        /// ticker can roll and so a test can assert the chance without a random source.
        /// </summary>
        public float LastGeigerChance { get; private set; }

        /// <summary>
        /// One frame of <c>UnitPlayer.actions()</c>'s radiation block.
        /// </summary>
        /// <returns>The hp the caller must write back, and whether the frame killed.</returns>
        public RadiationMath.TickResult Tick(
            float maxHp,
            float hp,
            float radX,
            float radChild,
            float ambientRad,
            float ambientWaterRad,
            bool inWater,
            bool invulnerable,
            bool godMode)
        {
            var input = new RadiationMath.TickInput
            {
                MaxHp = maxHp,
                Hp = hp,
                Rad = Rad,
                Drad = Drad,
                Drad2 = Drad2,
                RadX = radX,
                RadChild = radChild,
                AmbientRad = ambientRad,
                AmbientWaterRad = ambientWaterRad,
                InWater = inWater,
                NoRad = NoRad,
                Invulnerable = invulnerable,
                GodMode = godMode,
            };

            RadiationMath.TickResult result = RadiationMath.Tick(input);

            Rad = result.Rad;
            Drad = result.Drad;
            Drad2 = result.Drad2;
            NoRad = false;
            LastGeigerChance = result.GeigerChance;
            return result;
        }

        /// <summary>
        /// <c>UnitPlayer.udarUnit</c>'s tail (<c>:3218-3221</c>) — a landed hit carrying
        /// <c>radDamage</c> assigns <c>drad2</c>. It is an <b>assignment</b>, not an addition: the
        /// oracle writes <c>this.drad2 = param1.radDamage</c>, so a second radiating hit in the same
        /// frame replaces the first rather than stacking.
        /// </summary>
        public void AddHitRadiation(float amount)
        {
            if (amount <= 0f)
            {
                return;
            }

            Drad2 = amount;
        }

        /// <summary>
        /// One radioactive source's contribution for this frame — <c>Obj.ggModum</c>'s
        /// <c>gg.raddamage(normalised, strength, 0)</c> with the <c>radtip == 0</c> arm, i.e.
        /// <c>drad += strength * normalised</c> (<c>UnitPlayer.as:3234</c>).
        ///
        /// <para>Gated on <see cref="NoGasFrames"/>, which is the oracle's own first line.</para>
        /// </summary>
        public void AddSourceExposure(float normalised, float strength)
        {
            if (NoGasFrames > 0 || strength <= 0f || normalised <= 0f)
            {
                return;
            }

            Drad += strength * normalised;
        }

        /// <summary>
        /// The same source's elemental arm — <c>raddamage(..., tip)</c> with <c>tip != 0</c>
        /// (<c>UnitPlayer.as:3237-3249</c>). <paramref name="tip"/> 1 = venom, 2 = pink, 3 = necrotic;
        /// any other value is dropped exactly as the oracle drops it (<c>this["ddam" + tip] == null</c>).
        ///
        /// <para>Note the <b>step</b>: <see cref="RadiationMath.ElementalShare"/> is flat at full
        /// strength over the inner three quarters of the radius. That is the oracle, not a typo.</para>
        /// </summary>
        public void AddSourceElemental(float normalised, float strength, int tip)
        {
            if (NoGasFrames > 0 || strength <= 0f || normalised <= 0f)
            {
                return;
            }

            if (tip < 1 || tip > 3)
            {
                return;
            }

            _elemental[tip] += strength * RadiationMath.ElementalShare(normalised);
        }

        /// <summary>
        /// The 30-frame elemental payout (<c>UnitPlayer.as:1529-1551</c>).
        ///
        /// <para>Returns the three damage amounts in the oracle's own type order and zeroes the
        /// accumulators. Callers apply them as <c>DamageType.Venom</c> / <c>Pink</c> /
        /// <c>Necrotic</c> through the damage pipeline — never as a raw HP subtraction.</para>
        /// </summary>
        public void TickElemental(out float venom, out float pink, out float necrotic)
        {
            venom = pink = necrotic = 0f;

            ++_elementalClock;
            if (_elementalClock <= 30)
            {
                return;
            }

            _elementalClock = 0;
            venom = _elemental[1] / RadiationMath.FramesPerSecond;
            pink = _elemental[2] / RadiationMath.FramesPerSecond;
            necrotic = _elemental[3] / RadiationMath.FramesPerSecond;
            _elemental[1] = _elemental[2] = _elemental[3] = 0f;
        }

        /// <summary>
        /// <c>heal(amount, 2)</c> — RadAway (<c>Invent.as:430-433</c>) and the clinic's radiation row
        /// (<c>PipPageMed.as:298-305</c>). Floored at zero, and the caller refreshes the HUD.
        /// </summary>
        /// <returns>How much was actually removed, for the floating number.</returns>
        public float RemoveRad(float amount)
        {
            if (amount <= 0f || Rad <= 0f)
            {
                return 0f;
            }

            float before = Rad;
            Rad = RadiationMath.RadAfterRemoval(Rad, amount);
            return before - Rad;
        }

        /// <summary>
        /// <c>UnitPlayer.resurect()</c>'s clamp (<c>:3461-3467</c>) plus the 250-frame source grace
        /// (<c>:3481</c>) and the cleared hit remainder (<c>:3485</c>).
        /// </summary>
        public void OnResurrect(float maxHp)
        {
            Rad = RadiationMath.RadAfterResurrect(Rad, maxHp);
            NoGasFrames = 250;
            Drad2 = 0f;
            Drad = 0f;
        }

        /// <summary>
        /// <c>UnitPlayer.setLoc()</c> (<c>:654</c>) — a location change grants 90 frames without source
        /// damage, which is what stops a doorway between a clean room and a reactor from billing the
        /// player for the room they are leaving.
        /// </summary>
        public void OnLocationChanged()
        {
            NoGasFrames = 90;
        }

        /// <summary>Counts <see cref="NoGasFrames"/> down once per frame (<c>:1530-1533</c>).</summary>
        public void TickGrace()
        {
            if (NoGasFrames > 0)
            {
                --NoGasFrames;
            }
        }

        /// <summary>
        /// <c>UnitPlayer</c>'s new-game reset (<c>:3981</c>, <c>this.rad = cut = poison = 0</c>) and the
        /// fresh-spawn grace.
        /// </summary>
        public void Clear()
        {
            Rad = 0f;
            Drad = 0f;
            Drad2 = 0f;
            NoRad = true;
            NoGasFrames = 90;
            _elementalClock = 0;
            _elemental[1] = _elemental[2] = _elemental[3] = 0f;
        }

        /// <summary>
        /// Restore from a save. AS3's save carries <c>rad</c> but not the per-frame accumulators, so
        /// the grace is re-armed the same way a spawn arms it.
        /// </summary>
        public void Restore(float rad)
        {
            Clear();
            Rad = Mathf.Max(0f, rad);
        }
    }
}
