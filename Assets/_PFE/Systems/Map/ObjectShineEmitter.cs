using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Particles;
using PFE.Systems.Particles.Adapters;
using PFE.Systems.Physics;

namespace PFE.Systems.Map
{
    /// <summary>
    /// The port of AS3 <c>Interact.shine()</c> (<c>serv/Interact.as:1678-1686</c>) — the sparkle that
    /// plays over a door or container the instant it is unlocked or hacked.
    ///
    /// <para><b>The oracle, verbatim:</b></para>
    /// <code>
    /// Emitter.emit("unlock", this.loc, this.owner.X, this.owner.Y - this.owner.scY / 2, {
    ///     "kol": 10,
    ///     "rx": this.owner.scX,
    ///     "ry": this.owner.scY,
    ///     "dframe": 6
    /// });
    /// </code>
    /// <para>and the emitter row it names (<c>AllData.as:7013</c>):
    /// <c>&lt;part id='unlock' vis='flUnlock' minliv='17' anim='1' blend='screen'/&gt;</c> — a 25-frame
    /// screen-blended sprite that lives 17 ticks, spawned as ten copies spread over the object's own
    /// box and started at a random frame within the first six. The art is already in the port at
    /// <c>Assets/_PFE/Art/Particles/flUnlock/f000..f024.png</c>, so nothing has to be extracted: the
    /// particle pipeline just has to be asked.</para>
    ///
    /// <para><b>Two call sites, one anchor rule.</b> The scripted unlock
    /// (<c>AreaTriggerSystem</c>'s <c>unlock</c> command, which is how <c>rooms_begin/room_2_0</c>'s
    /// key-only <c>begDoor</c> opens) and the object verbs <c>hack</c>/<c>unlock</c>
    /// (<c>ObjectVerbCommand</c>, via <c>ObjectVerbHost.Shine</c>) are the same flash in the oracle, so
    /// they share this function rather than each computing a position.</para>
    ///
    /// <para><b>The one measured divergence: what "scX/scY" means.</b> AS3 reads the <i>rendered
    /// sprite's</i> size — <c>scX = vis.width</c> (<c>Box.as:158</c>), default 10 before the art is
    /// built. The map layer has no presenter and therefore no sprite, so this uses the object's
    /// <b>authored extent</b>: <c>@sX</c>/<c>@sY</c> when present, else <c>@size * 40</c> — the same
    /// resolution <c>Trap.as:79-94</c> uses and <see cref="TrapTriggerMath"/> already implements. For a
    /// door or a container whose art matches its tile footprint the two agree; where the art is larger
    /// the sparkles are spread over a smaller box than AS3 would use. That is a bounded cosmetic
    /// difference, stated rather than hidden.</para>
    /// </summary>
    public static class ObjectShineEmitter
    {
        /// <summary>The <c>&lt;part&gt;</c> row AS3 names (<c>AllData.as:7013</c>).</summary>
        public const string ParticleId = "unlock";

        /// <summary>AS3 <c>param4.kol</c> — ten copies per call.</summary>
        public const int Kol = 10;

        /// <summary>AS3 <c>param4.dframe</c> — the start frame is jittered over the first six.</summary>
        public const int DFrame = 6;

        private static bool s_reportedNoEmitter;

        /// <summary>
        /// Emits the flash over <paramref name="obj"/>, in <paramref name="room"/>'s space.
        ///
        /// <para>Returns whether a particle was actually spawned. False covers three distinct cases
        /// that <see cref="RoomParticleEmitter.Emit"/> itself keeps apart — no room pushed into the
        /// emitter, an id missing from the particle table, and a budget refusal — plus the fourth this
        /// adds: no emitter handed down at all, which is reported once by name because a silently
        /// unbound shine is exactly the state this type was written to end.</para>
        /// </summary>
        public static bool Emit(RoomParticleEmitter emitter, RoomInstance room, ObjectInstance obj)
        {
            if (obj == null || room == null) return false;

            if (emitter == null)
            {
                if (!s_reportedNoEmitter)
                {
                    s_reportedNoEmitter = true;
                    Debug.LogWarning(
                        "[Shine] Interact.shine() reached an object but no RoomParticleEmitter has been " +
                        "handed to the caller, so the unlock sparkle is skipped. The flash is emitted " +
                        "from the map layer — see RoomVisualController.SetParticleAdapters.");
                }

                return false;
            }

            ResolveExtent(obj, out float scX, out float scY);

            // AS3 `X, Y - scY/2`: the object's vertical middle. `obj.position` is the port's bottom
            // centre in ROOM-LOCAL pixels (Y up), so the middle is half its height ABOVE it, and the
            // room origin has to be added back because `RoomParticleEmitter.Emit` takes a Unity
            // WORLD position and subtracts that origin itself.
            Vector2 worldPixels = obj.position
                                  + WorldCoordinates.RoomOriginPixel(room)
                                  + new Vector2(0f, scY * 0.5f);

            Vector3 worldPos = WorldCoordinates.PixelToUnity(worldPixels);

            var spec = new ParticleSpec
            {
                Kol = Kol,
                RX = scX,
                RY = scY,
                DFrame = DFrame,
            };

            return emitter.Emit(ParticleId, worldPos, spec);
        }

        /// <summary>
        /// The object's authored pixel extent — <c>@sX</c>/<c>@sY</c> when positive, else
        /// <c>@size * 40</c>. See the type doc for why this is not <c>vis.width</c>.
        /// </summary>
        public static void ResolveExtent(ObjectInstance obj, out float scX, out float scY)
        {
            MapObjectDefinition definition = obj?.definition;

            int size = ReadInt(definition, "size", 1);
            int wid = ReadInt(definition, "wid", size);

            scX = TrapTriggerMath.ResolveExtentX(size, ReadFloat(definition, "sX"));
            scY = TrapTriggerMath.ResolveExtentY(wid, ReadFloat(definition, "sY"));
        }

        private static int ReadInt(MapObjectDefinition definition, string key, int fallback)
        {
            if (definition == null) return fallback;

            return int.TryParse(
                definition.GetAttribute(key, string.Empty),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int value)
                ? value
                : fallback;
        }

        private static float ReadFloat(MapObjectDefinition definition, string key)
        {
            if (definition == null) return 0f;

            return float.TryParse(
                definition.GetAttribute(key, string.Empty),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float value)
                ? value
                : 0f;
        }
    }
}
