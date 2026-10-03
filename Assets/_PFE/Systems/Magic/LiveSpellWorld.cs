using System;
using UnityEngine;
using PFE.Systems.Audio;
using PFE.Systems.Map.TileQuery;

namespace PFE.Systems.Magic
{
    /// <summary>
    /// The production <see cref="ISpellWorld"/> — what a cast needs from the world <i>around</i> the
    /// caster: the line-of-sight test, the alicorn flag, and the three presentation channels.
    ///
    /// <para><b>Deliberately not a <c>MonoBehaviour</c>.</b> It holds no state of its own and reads the
    /// world through two interfaces, so it is a plain class the caster constructs once. That also keeps
    /// it out of the <c>ECall</c> wall: <c>Vector2</c> and <c>Mathf</c> are fine offline, and the two
    /// collaborators are interfaces a fixture can fake — so unlike the caster, this class is provable
    /// without an editor. See <see cref="LiveSpellHost"/> for why the seam is split this way.</para>
    ///
    /// <para><b>The room is a <see cref="Func{TResult}"/>, not a captured reference.</b> AS3's
    /// <c>loc.isLine</c> reads the <i>current</i> <c>loc</c>, which is swapped on every room transition.
    /// A tile query captured at construction would freeze on the room the caster was built in — the same
    /// failure <c>PlayerTelekinesisController.ResolveTileQuery</c> documents and rebuilds around. The
    /// caster owns the room tracking; this class only asks.</para>
    /// </summary>
    public sealed class LiveSpellWorld : ISpellWorld
    {
        private readonly Func<ITileQueryService> _tileQuery;
        private readonly ISoundService _sound;

        /// <param name="tileQuery">
        /// Resolves the tile query for the caster's <i>current</i> room, or returns <c>null</c> when no
        /// room is available. Called once per line-of-sight test, never cached here.
        /// </param>
        /// <param name="sound">The sound service, or <c>null</c> for a rig with no audio.</param>
        public LiveSpellWorld(Func<ITileQueryService> tileQuery, ISoundService sound)
        {
            _tileQuery = tileQuery;
            _sound = sound;
        }

        /// <summary>
        /// AS3 <c>World.w.alicorn</c> — <b>always false, because the port has no such flag.</b>
        ///
        /// <para>This is a deliberate, recorded gap rather than an oversight. AS3's <c>alicorn</c> is a
        /// global toggled when the player becomes the alicorn; the port has no playable alicorn, which
        /// is the same reason <c>PlayerManaTicker</c> leaves its <c>AlicornRunning</c> branch false. The
        /// nearest things the port does have are per-<i>definition</i> flags
        /// (<c>UnitDefinition.isAlicorn</c>, <c>WeaponDefinition.alicornOnly</c>) — neither is this
        /// runtime global.</para>
        ///
        /// <para><b>Left false rather than wired to something plausible</b> so the two alicorn branches
        /// (the global refusal and the Def-key substitution) stay visibly unimplemented instead of
        /// becoming accidentally reachable. When a playable alicorn lands, this is the one member to
        /// give a producer.</para>
        /// </summary>
        bool ISpellWorld.Alicorn => false;

        /// <summary>
        /// AS3 <c>owner.loc.isLine(X, Y, cx, cy)</c>, <b>inverted</b> — true when something solid blocks
        /// the segment.
        ///
        /// <para><b>The unit conversion lives here, and this is the only place it should.</b> The cast
        /// path works in <b>Unity units</b>: <c>Spell.X</c>/<c>Y</c> come from the weapon mount point
        /// (<c>WeaponMounts.MagicHoldPoint</c>), and the target is the same aim point
        /// <c>MagicWeaponController</c> rotates toward — both Unity world positions. The port's tile
        /// primitive, however, is documented and implemented in <b>world pixels</b>
        /// (<c>TileCollisionSystem.Raycast</c> subtracts the room origin from a pixel position). So the
        /// ×<see cref="TileQueryConstants.UnitToPixel"/> belongs at the boundary between the two, which
        /// is this method — not at the caster, which would then disagree with the magic-weapon path, and
        /// not at the caller, which cannot know.</para>
        ///
        /// <para><b>A missing room means "nothing blocks".</b> That is the honest reading: with no room
        /// there are no tiles, so the ray cannot hit anything. It is <i>not</i> the same as "line of
        /// sight failed", and it must not be — refusing a cast because the room was not yet streamed in
        /// would be a refusal the player cannot see or fix. The cast's own gate order decides what a
        /// failed line means; this method only reports what it found.</para>
        /// </summary>
        bool ISpellWorld.IsBlocked(float fromX, float fromY, float toX, float toY)
        {
            ITileQueryService query = _tileQuery?.Invoke();
            if (query == null) return false;

            var originPx = new Vector2(
                fromX * TileQueryConstants.UnitToPixel,
                fromY * TileQueryConstants.UnitToPixel);
            var targetPx = new Vector2(
                toX * TileQueryConstants.UnitToPixel,
                toY * TileQueryConstants.UnitToPixel);

            Vector2 delta = targetPx - originPx;
            float distance = delta.magnitude;

            // A degenerate segment cannot be blocked — there is nothing to travel through. Guarded
            // rather than left to `delta / distance`, which would divide by zero and hand the query a
            // NaN direction (a ray that reports "no hit" everywhere, i.e. a silently permissive LOS).
            if (distance <= 1e-4f) return false;

            TileRaycastHit? hit = query.Raycast(originPx, delta / distance, distance);

            // `Tile != null`, not merely `HasValue`: the interface documents a hit whose source is not
            // a tile, and a non-tile hit is not a wall. Mirrors PlayerTelekinesisController's own test.
            return hit.HasValue && hit.Value.Tile != null;
        }

        /// <summary>
        /// AS3 <c>Snd.ps(id, x, y)</c> (<c>Spell.as:287</c>, and the <c>"nomagic"</c> refusal cue).
        /// A null service is silent, which is the correct behaviour for a rig with no audio rather than
        /// an error.
        /// </summary>
        void ISpellWorld.PlaySound(string soundId, float x, float y)
        {
            if (string.IsNullOrEmpty(soundId)) return;
            _sound?.Play(soundId, new Vector2(x, y));
        }

        /// <summary>
        /// AS3 <c>World.w.gui.infoText(key, null, null, false)</c> — <b>a no-op, and knowingly so.</b>
        ///
        /// <para>The port has no localised message layer: there is no <c>gui.infoText</c> and no string
        /// table, so <c>"disSpell"</c>, <c>"noSpells"</c>, <c>"overMana"</c>, <c>"noMana"</c> and
        /// <c>"noVisible"</c> have nowhere to land. The same gap is recorded on
        /// <c>HitAvoidance</c> and <c>MagicWeaponController</c> for their own refusals.</para>
        ///
        /// <para><b>It is a no-op rather than a log line on purpose.</b> A log line here would print on
        /// every refused cast of every spell — including the silent-in-AS3 <c>alicorn</c>/<c>rat</c>
        /// refusals the caller already suppresses — and, worse, it would read as "the message system
        /// worked". A message that cannot be false is how this project has been sent to the wrong half
        /// of a system before. When the layer lands, these three members are the only ones to change.</para>
        /// </summary>
        void ISpellWorld.ShowInfoText(string key)
        {
        }

        /// <inheritdoc cref="ISpellWorld.ShowInfoText(string)"/>
        void ISpellWorld.ShowInfoText(string key, float number)
        {
        }

        /// <summary>
        /// AS3 <c>World.w.gui.bulb(x, y)</c> — the on-screen attention marker. No-op for the same reason
        /// as <see cref="ISpellWorld.ShowInfoText(string)"/>: the port has no bulb renderer.
        /// </summary>
        void ISpellWorld.ShowBulb(float x, float y)
        {
        }
    }
}
