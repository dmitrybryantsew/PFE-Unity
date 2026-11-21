namespace PFE.Entities.Units
{
    /// <summary>
    /// The view-side source of the avatar extras a unit replicates in <c>SimUnitState</c> —
    /// aim angle, equipped-weapon id and the animation state/frame the body's
    /// <c>CharacterAnimationDriver</c> is currently playing.
    ///
    /// <para><b>Why a seam instead of reads inside <c>CaptureSimState</c>.</b> The truth for these
    /// four values lives on OTHER components of the rig: the aim on the controller/loadout, the
    /// weapon on the loadout, the animation on the sprite assembler. <c>UnitController</c> cannot
    /// reach for them without hard-wiring the whole rig, and the simulated multiplayer body —
    /// which has no <c>PlayerController</c> at all — would have nothing to hand over. The rig's
    /// owner (the local <c>PlayerController</c>, or <c>PfeRemoteBody</c> for a simulated peer)
    /// installs one provider and the shared capture path stamps the record from it.</para>
    ///
    /// <para><b>Pure data, no engine types.</b> This is a replication seam: everything it returns
    /// must already be in the wire vocabulary (<c>RemoteAnimStateIds</c> byte, the
    /// <c>LootItemKey</c> weapon hash, plain degrees).</para>
    /// </summary>
    public interface IUnitAvatarViewSource
    {
        /// <summary>Aim facing in degrees, the <c>PlayerCommand.AimAngle</c> convention
        /// (world-space <c>atan2</c>, −90 up, 0 +X, +90 down).</summary>
        float AimAngle { get; }

        /// <summary>The equipped weapon's content id as <c>LootItemKey(id, true)</c>; 0 = unarmed.</summary>
        ulong WeaponIdHash { get; }

        /// <summary>The body's current animation state as a <c>RemoteAnimStateIds</c> id;
        /// <c>RemoteAnimStateIds.Unknown</c> when the state name has no agreed id.</summary>
        byte AnimStateId { get; }

        /// <summary>Frame within that state's clip, as the assembler reports it.</summary>
        ushort AnimFrame { get; }
    }
}
