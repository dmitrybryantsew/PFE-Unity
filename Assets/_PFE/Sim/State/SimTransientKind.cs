namespace PFE.Sim.State
{
    /// <summary>
    /// What kind of live, transient entity a <see cref="SimProjectileState"/> describes.
    ///
    /// <para><b>Why one record and one discriminator, rather than three records.</b> A fired round, a
    /// thrown grenade and a placed mine differ in <i>behaviour</i> — a round homes, a grenade bounces,
    /// a mine arms and waits — but not in the state a client needs to draw them and step their next
    /// frame: identity, position, velocity, definition, lifetime, latches. Three records with the same
    /// twenty fields would be three places for a field to drift, which is the defect class this project
    /// keeps paying for. So the array is the <i>transient entity</i> list and the kind says which
    /// behaviour the receiver should apply.</para>
    ///
    /// <para><b>Why <see cref="Projectile"/> is 0.</b> <c>default(SimProjectileState)</c> — a
    /// hand-built record in a fixture, or a field a producer forgot to stamp — then reads as the
    /// commonest kind rather than as an unknown one. A zero value that means "nothing" would make every
    /// unstamped record a silent drop on the client.</para>
    ///
    /// <para><b>Where the kinds come from.</b> The three are exactly the classes that hold a host-minted
    /// <c>EntityId</c> without a room record behind them (row 3.2's registry is record-keyed, so these
    /// self-register). Units are not here: they have a record, they live in
    /// <see cref="SimRoomDelta.Units"/>, and their behaviour is selected by
    /// <c>SimUnitState.ControllerSlot</c>, not by a kind.</para>
    /// </summary>
    public static class SimTransientKind
    {
        /// <summary>A fired round (<c>Projectile</c>) — the default, and the only kind before 3.3.</summary>
        public const byte Projectile = 0;

        /// <summary>A thrown object (<c>ThrownObject</c>) — bounces, then fuses.</summary>
        public const byte ThrownObject = 1;

        /// <summary>A placed mine (<c>MineObject</c>) — arms, senses, detonates.</summary>
        public const byte Mine = 2;

        /// <summary>The highest kind this build understands.</summary>
        public const byte Max = Mine;

        /// <summary>True when <paramref name="kind"/> is a kind this build can interpret.</summary>
        public static bool IsKnown(byte kind) => kind <= Max;

        /// <summary>The kind's name, for logs and <c>ToString</c>. Never throws on an unknown byte.</summary>
        public static string NameOf(byte kind)
        {
            switch (kind)
            {
                case Projectile: return "projectile";
                case ThrownObject: return "thrown";
                case Mine: return "mine";
                default: return "unknown(" + kind + ")";
            }
        }
    }
}
