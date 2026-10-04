using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using UnityEngine;

namespace PFE.Systems.Particles.Adapters
{
    /// <summary>
    /// The Unity-side "emit a particle here" seam: converts a <b>Unity world position</b> into the
    /// <b>AS3 room-local pixel</b> space <see cref="IParticleWorld"/> works in, and forwards the emit.
    ///
    /// <para><b>Why this type exists at all.</b> The particle pipeline has roughly ninety call sites
    /// across twenty files — bullets, spells, blood sprays, status effects, debris, teleports, box
    /// breaks. Every one of them starts from a Unity world position (an impact point, a unit's feet, a
    /// spell's target) and every one of them needs the same conversion. Written per caller that is the
    /// same three lines copy-pasted ninety times, with the Y mirror wrong in whichever copy was
    /// written last. Written once, here, it is one function that a fixture can pin. The callers then
    /// read as what they mean: <c>emitter.Emit("expl", impactPos)</c>.</para>
    ///
    /// <para><b>The conversion is not a scale.</b> Two steps, and both are load-bearing:
    /// <see cref="WorldCoordinates.UnityWorldToAs3RoomLocal"/> subtracts the room's origin (a Unity
    /// world position is a <i>world</i> pixel, and the room's origin is not the world origin in any
    /// room but the first) and then mirrors Y (AS3 runs Y downward from the ceiling, the port's
    /// room-local space runs upward from the floor). Skipping the mirror does not fail loudly — it
    /// emits at the <i>vertically opposite</i> point, which for a ceiling impact is a whole room
    /// height away.</para>
    ///
    /// <para><b>The service is pushed, not injected, exactly like <see cref="TileQueryParticleWater"/>.</b>
    /// Water and the room origin are both properties of <i>the current room</i>, so a container
    /// singleton holding either would be wrong the moment the player changes rooms. Until a room pushes
    /// a service this refuses to emit — see below.</para>
    ///
    /// <para><b>Refusing is the point, not a shortcoming.</b> With no room there is no origin and no
    /// height, so there is no correct answer; emitting at an unmirrored, unoffset position would put
    /// the particles somewhere plausible and wrong, which is precisely the failure mode this class
    /// exists to remove. <see cref="Emit"/> therefore returns <c>false</c> and counts the refusal in
    /// <see cref="RefusedWithoutRoom"/>, which is the readback for "particles never appear" that does
    /// not require guessing.</para>
    /// </summary>
    public sealed class RoomParticleEmitter
    {
        private readonly IParticleWorld _world;
        private ITileQueryService _tiles;
        private int _refusedWithoutRoom;

        /// <summary>
        /// Wires the world. The room's tile query arrives later, through <see cref="SetTileQuery"/>.
        /// </summary>
        public RoomParticleEmitter(IParticleWorld world)
        {
            _world = world;
        }

        /// <summary>
        /// True once a room has pushed a tile service. False means <see cref="Emit"/> refuses — the
        /// readback for "an explosion did nothing".
        /// </summary>
        public bool HasRoom => _tiles != null && _tiles.Room != null;

        /// <summary>Emits refused because no room was pushed. Non-zero means a wiring fault, not a data one.</summary>
        public int RefusedWithoutRoom => _refusedWithoutRoom;

        /// <summary>
        /// Hands the emitter the current room's tile service — the same push, from the same place, as
        /// <see cref="TileQueryParticleWater.SetTileQuery"/>. Pass null on teardown so a stale room
        /// cannot place particles for the next one.
        /// </summary>
        public void SetTileQuery(ITileQueryService tiles) => _tiles = tiles;

        /// <summary>
        /// Unity world position → AS3 room-local pixels. False when no room has been pushed, in which
        /// case <paramref name="as3Local"/> is left at the origin and must not be used.
        /// </summary>
        public bool TryToAs3Local(Vector3 worldPos, out Vector2 as3Local)
        {
            as3Local = Vector2.zero;

            if (_tiles == null) return false;

            RoomInstance room = _tiles.Room;
            if (room == null) return false;

            as3Local = WorldCoordinates.UnityWorldToAs3RoomLocal(worldPos, _tiles.OriginPixel, room.height);
            return true;
        }

        /// <summary>
        /// Emits <paramref name="id"/> at a Unity world position. This is the call every gameplay site
        /// should use; it is the whole reason the type exists.
        /// </summary>
        /// <returns>
        /// Whatever <see cref="IParticleWorld.Emit"/> answered — so <c>false</c> covers three distinct
        /// cases that are each reported somewhere: no room (<see cref="RefusedWithoutRoom"/>), an id
        /// that is not in the table (<see cref="IParticleWorld.UnknownIds"/>), and a budget refusal
        /// (<see cref="ParticleWorld.Budget"/>). It never means "nothing to do".
        /// </returns>
        public bool Emit(string id, Vector3 worldPos, ParticleSpec overrides = null)
        {
            if (_world == null) return false;

            if (!TryToAs3Local(worldPos, out Vector2 as3Local))
            {
                _refusedWithoutRoom++;
                return false;
            }

            return _world.Emit(id, as3Local.x, as3Local.y, overrides);
        }

        /// <summary>
        /// Emits at a point that is <b>already</b> in AS3 room-local pixels — for a caller that is
        /// working in the oracle's own space and has no Unity position to convert (an AS3-space
        /// simulation, or a replay). Gameplay sites should prefer <see cref="Emit"/>; going through
        /// this one from Unity space is how the mirror gets skipped.
        /// </summary>
        public bool EmitAt(string id, Vector2 as3Local, ParticleSpec overrides = null)
        {
            return _world != null && _world.Emit(id, as3Local.x, as3Local.y, overrides);
        }
    }
}
