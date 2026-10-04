using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Optional target-side read for the damage path's blood spray — AS3 <c>Unit.blood</c>
    /// (<c>Unit.as:416</c>) and the unit's sprite size (<c>scX</c>/<c>scY</c>).
    ///
    /// <para><b>Why this is a separate interface rather than two more members on
    /// <see cref="IDamageable"/>.</b> The members that already sit on <see cref="IDamageable"/> —
    /// <c>Armour</c>, <c>Vulnerabilities</c>, <c>SkinResistance</c>, <c>Evasion</c>, <c>Knocked</c>,
    /// <c>Mass</c> — are all there by one rule: <i>the damage formula reads them</i>. Blood is not part
    /// of the formula. It is presentation, and putting it on the combat interface would mean every
    /// damageable in the game — including a crate — has to answer a question about gore. The narrow
    /// interface expresses the oracle's own shape instead: AS3's blood block lives inside
    /// <c>Unit.damage()</c>, so it is a <c>Unit</c> behaviour, and a target that is not a unit simply
    /// does not implement this — the same reasoning <c>IEffectReceiver</c> is built on.</para>
    ///
    /// <para><b>Both reads come from the definition, not from live state.</b> AS3 never mutates
    /// <c>blood</c> after construction, and <c>scX</c>/<c>scY</c> are authored sizes, so there is no
    /// runtime copy to prefer — unlike <c>Armour</c> and <c>SkinResistance</c>, which
    /// <c>UnitStats</c> owns because they change.</para>
    /// </summary>
    public interface IBloodSpraySource
    {
        /// <summary>
        /// AS3 <c>Unit.blood</c> — <c>0</c> = none, <c>1</c> = red, <c>2</c> = green, <c>3</c> = pink.
        ///
        /// <para><b>Zero is not "unset".</b> It is the authored "this creature does not bleed", and it
        /// carries a combat meaning as well as a visual one: <c>Unit.as:1417-1420</c> makes a
        /// <c>blood == 0</c> unit <b>immune to bleed</b>. A target with no definition answers
        /// <see cref="BloodType.None"/>, which is AS3's own field default — never
        /// <see cref="BloodType.Red"/>, which would spray gore from every uninitialised object.</para>
        /// </summary>
        BloodType BloodType { get; }

        /// <summary>
        /// The unit's authored sprite size in <b>AS3 pixels</b> — AS3 <c>scX</c>/<c>scY</c>, from
        /// <c>&lt;phis sX sY&gt;</c>.
        ///
        /// <para>Deliberately pixels rather than Unity units: every consumer of this value is working in
        /// the AS3 room-local pixel space the particle pipeline integrates in (<c>ParticleState.X/Y</c>),
        /// and handing back Unity units would put a hidden ×100 in the middle of a coordinate
        /// conversion — the exact class of bug lesson #51 is about.</para>
        /// </summary>
        Vector2 SpriteSizePixels { get; }

        /// <summary>
        /// This target's origin in <b>Unity world space</b> — the anchor AS3's blood block calls
        /// <c>X</c>/<c>Y</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the origin and not the sprite centre.</b> AS3's own box definition settles it:
        /// <c>Unit.as:1875-1878</c> gives <c>Y1 = Y - scY</c> (the top), <c>Y2 = Y</c> (the bottom),
        /// <c>X1 = X - scX / 2</c> and <c>X2 = X + scX / 2</c>. So <c>X</c> is the horizontal
        /// <i>centre</i> — but <c>Y</c> is the <i>bottom</i>, the feet, not the vertical centre. In the
        /// port <c>UnitController.transform.position</c> is exactly that origin; see
        /// <c>UnitController.FeetWorldY</c>, which three independent places agree on.</para>
        ///
        /// <para><b>This is load-bearing, not a naming preference.</b> The blood block treats
        /// <c>X</c>/<c>Y</c> as the reference and expresses every emission as an offset from it — the
        /// standing spray is <c>Y - scY / 2</c>, the gib is <c>Y - rand * scY * 0.5 - 40</c>. Feeding the
        /// sprite centre in as the anchor and then applying those offsets would put the spray a further
        /// half-height up, which is why this exposes the oracle's own reference point rather than a
        /// "nicer" one. (The plan's §15.4 originally said "the unit's sprite centre, since AS3's
        /// <c>X</c>/<c>Y</c> is the centre (<c>X1 = X - scX/2</c>, <c>Y - scY/2</c>)" — the X half is
        /// right and the Y half is not, and the corrected reading is above.)</para>
        ///
        /// <para><b>Unity world space, because the caller owns the conversion.</b> Turning this into AS3
        /// room-local pixels needs the current room's origin and height, which live behind
        /// <c>RoomParticleEmitter.TryToAs3Local</c> and are pushed in per room. Returning pixels here
        /// would put a room dependency on every damageable in the game.</para>
        /// </remarks>
        Vector3 WorldPosition { get; }
    }
}
