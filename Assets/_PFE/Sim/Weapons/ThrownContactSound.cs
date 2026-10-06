using UnityEngine;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// The tile-contact ("landing") sound of a thrown object — AS3 <c>PhisBullet</c>'s
    /// <c>sndHit</c>, which <c>WThrow</c> fills from the weapon's <c>&lt;snd fall&gt;</c>.
    ///
    /// <para><b>The oracle.</b> <c>WThrow.as:196</c> assigns
    /// <c>(b as PhisBullet).sndHit = this.sndFall</c>, where <c>sndFall</c> came from
    /// <c>node.snd[0].@fall</c> (<c>:64-66</c>), and <c>PhisBullet.run()</c> then plays it on tile
    /// contact at five sites — <c>:246</c>, <c>:267</c>, <c>:290</c>, <c>:321</c>, <c>:336</c> — each
    /// as <c>Snd.ps(this.sndHit, X, Y, 0, Math.abs(d / 10))</c>, where <c>d</c> is the velocity of
    /// the axis that struck, in px/frame.</para>
    ///
    /// <para><b>Why this is a type and not two lines at the call site.</b> The volume expression is
    /// the whole of the rule, and it is the kind of thing this project has repeatedly got wrong by
    /// guessing a unit: <c>d</c> is a px/frame velocity, not the port's units/s, so a caller that
    /// passed <c>_velocity.x</c> straight in would be 3.33× off and would never notice, because a
    /// louder bang still sounds like a bang. Pinned by fixtures in
    /// <c>ThrownContactSoundTests</c> instead.</para>
    ///
    /// <para><b>The volume is not clamped.</b> AS3 multiplies <c>param5</c> straight into the
    /// <c>SoundTransform</c> (<c>Snd.as:637-663</c>) with no clamp, so a grenade arriving at
    /// 20 px/frame is asked for <c>2.0</c> — twice its base volume. Clamping to 1 here would make
    /// every fast landing quieter than the oracle's and every slow one identical to it, which is the
    /// opposite of what the sound is for.</para>
    /// </summary>
    public static class ThrownContactSound
    {
        /// <summary>
        /// AS3's <c>|d| / 10</c> divisor (<c>PhisBullet.as:246</c> and the four siblings). Ten
        /// px/frame — a third of <c>WThrow</c>'s own <c>speed='20'</c> — is the reference speed that
        /// maps to volume 1.0.
        /// </summary>
        public const float VolumeDivisor = 10f;

        /// <summary>
        /// Whether there is anything to play. An empty id is the common case — only thrown weapons
        /// carry <c>&lt;snd fall&gt;</c>, and a weapon without one lands silently rather than
        /// falling back to a generic thud.
        /// </summary>
        public static bool ShouldPlay(string soundId) => !string.IsNullOrEmpty(soundId);

        /// <summary>
        /// The volume multiplier for a contact, from the striking axis's speed in <b>px/frame</b>
        /// (AS3 units). See <see cref="ProjectilePhysicsMath.VelocityScale"/> for the conversion out
        /// of the port's units/s — passing units/s here is the mistake this signature exists to make
        /// visible.
        /// </summary>
        public static float VolumeScale(float axisSpeedPxPerFrame)
            => Mathf.Abs(axisSpeedPxPerFrame) / VolumeDivisor;
    }
}
