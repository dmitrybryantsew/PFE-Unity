namespace PFE.Systems.Particles
{
    /// <summary>
    /// One particle a rules table asks for: the row id, an optional override spec, and an offset from
    /// the caller's anchor point.
    /// </summary>
    /// <remarks>
    /// <para><b>Why offsets rather than a pre-baked position.</b> Every producer works in the AS3
    /// room-local pixel space the particle pipeline integrates in (<c>ParticleState.X/Y</c>), and the
    /// anchor differs per family — an explosion's impact point, a unit's feet, a bullet's contact. So
    /// the rule carries the <i>delta</i> and the caller adds it to its own anchor. That keeps this type
    /// free of any coordinate opinion, and keeps the rules Unity-free, which is what makes them testable
    /// offline.</para>
    ///
    /// <para><b>The X offset exists for the blood spray.</b> AS3's spray is thrown sideways
    /// (<c>X + 80 * dir + …</c>, <c>Unit.as:3897</c>) as well as upward, so a Y-only offset could not
    /// express it. Sharing one struct rather than growing a second near-identical one is deliberate:
    /// slice 5's effect family and slice 6's long tail both need the same shape.</para>
    /// </remarks>
    public readonly struct ParticleEmit
    {
        /// <summary>The <c>&lt;part&gt;</c> row id to emit.</summary>
        public readonly string Id;

        /// <summary>Spawn overrides, or null for the row's own defaults.</summary>
        public readonly ParticleSpec Spec;

        /// <summary>Added to the anchor's AS3 room-local X. Positive is right.</summary>
        public readonly float OffsetX;

        /// <summary>
        /// Added to the anchor's AS3 room-local Y. <b>AS3 Y runs DOWN</b>, so a negative value is
        /// <i>upward</i> — which is why <c>balefire</c>'s <c>Y - 60</c> arrives here as
        /// <c>-60</c>.
        /// </summary>
        public readonly float OffsetY;

        public ParticleEmit(string id, ParticleSpec spec = null, float offsetX = 0f, float offsetY = 0f)
        {
            Id = id;
            Spec = spec;
            OffsetX = offsetX;
            OffsetY = offsetY;
        }
    }
}
