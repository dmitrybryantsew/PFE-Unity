using System;

namespace PFE.Core.Ids
{
    /// <summary>
    /// The identity rule for floor loot — §10.2 gap 2 on the <i>producing</i> side.
    ///
    /// <para><b>Why a record needs an id at all.</b> <c>SimDroppedItem</c> is keyed by a host-assigned
    /// <c>EntityHash</c>, not by <c>(content id, position)</c>, because two identical stacks can lie on
    /// one pixel and because a stack that moves is the same stack. A record that reaches the projector
    /// without an id cannot be expressed on the wire at all: the receiver rejects a zero id as exactly
    /// the conflation §10.2 removed. So the id has to exist from the moment the record is created, not
    /// from the moment someone remembers to ask for one.</para>
    ///
    /// <para><b>Why this is a separate rule and not two lines inside the room.</b> The room is a Unity
    /// type in the view assembly; a rule that lives there can only be checked by a source lint. Here it
    /// is a pure function the offline wall can execute, and the lint is left to pin the one thing it is
    /// good at — that <c>RoomInstance</c> actually calls it.</para>
    ///
    /// <para><b>Idempotent by construction.</b> A record that already carries an id keeps it. The
    /// restore path rebuilds records and re-adds them; minting on every add would give one stack a new
    /// identity every time a room was rebuilt, which is the same as having no identity at all.</para>
    /// </summary>
    public static class DroppedItemIdentityRules
    {
        /// <summary>The "no identity yet" value. Never a legal <c>SimDroppedItem.EntityHash</c>.</summary>
        public const ulong Unassigned = 0UL;

        /// <summary>
        /// The id a floor-loot record must carry: the one it already has, or a freshly minted one.
        ///
        /// <para>An invalid mint yields <see cref="Unassigned"/> on its own — <c>EntityId.IsValid</c> is
        /// exactly <c>Hash != 0</c>, so there is no separate "the allocator lied" branch to write. That
        /// is deliberate: a branch that cannot change the answer is a guard that cannot fail, and a
        /// record left unassigned is refused by the receiver's <c>CorruptLoot</c> guard rather than
        /// silently stored under a duplicate id.</para>
        /// </summary>
        /// <param name="existingHash">The record's current id, or <see cref="Unassigned"/>.</param>
        /// <param name="mint">Mints the next host id. May be null in a context with no allocator.</param>
        public static ulong Resolve(ulong existingHash, Func<EntityId> mint)
        {
            if (existingHash != Unassigned)
            {
                return existingHash;
            }

            if (mint == null)
            {
                return Unassigned;
            }

            return mint().Hash;
        }
    }
}
