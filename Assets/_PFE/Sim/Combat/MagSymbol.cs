using UnityEngine;

namespace PFE.Systems.Combat
{
    /// <summary>What one <see cref="MagSymbol.Step"/> did — the three beats of <c>MagSymbol.step()</c>.</summary>
    [System.Flags]
    public enum MagSymbolStep
    {
        /// <summary>Nothing this tick.</summary>
        None = 0,

        /// <summary><c>Emitter.emit("magsymbol", loc, X, Y)</c> — the circle appeared.</summary>
        Emitted = 1,

        /// <summary><c>spellCast()</c> — this is the tick the curse lands on.</summary>
        Applied = 2,

        /// <summary><c>loc.remObj(this)</c> — drop it.</summary>
        Expired = 4,
    }

    /// <summary>
    /// One curse circle — <c>fe/weapon/MagSymbol.as</c>, as a plain object rather than a display object.
    ///
    /// <para><b>Why not a <c>MonoBehaviour</c>.</b> In AS3 a <c>MagSymbol</c> is an <c>Obj</c> on the
    /// location's list; it has no physics, no collision and no sprite — only a position, a countdown and
    /// one effect application. A GameObject per circle would buy nothing and cost a lifecycle (register
    /// on the sim loop, unregister on destroy, survive a room teardown). The owner keeps a list and steps
    /// it, and the whole object is testable without a scene.</para>
    ///
    /// <para><b>The step order is the oracle's, and it matters.</b> <c>step()</c> tests
    /// <c>liv == 20</c> <i>before</i> decrementing, then applies at <c>liv == 1</c> <i>after</i>
    /// decrementing. So a symbol constructed with <c>liv = 20</c> emits on its first tick and applies on
    /// its <b>nineteenth</b> — eighteen ticks of warning — and one constructed with <c>liv = 36</c> emits
    /// on its seventeenth tick and applies on its thirty-fifth. The 16-tick difference between those two
    /// application times is exactly <c>param5</c>, and it is what makes a five-circle line sweep instead
    /// of landing all at once.</para>
    /// </summary>
    public sealed class MagSymbol
    {
        /// <summary>
        /// Where the circle sits, <b>in AS3 room-local pixels</b> — Y grows down, 0 is the top of the
        /// room. Not this port's <c>worldPx</c>; see <see cref="MagSymbolRules"/> for why the space is
        /// kept as the oracle's.
        /// </summary>
        public readonly Vector2 PositionAs3Pixels;

        /// <summary>
        /// The effect id to apply on arrival — <c>spellId</c>. One of
        /// <see cref="BossNecrRules.CurseFor"/>'s nine.
        /// </summary>
        public readonly string SpellId;

        /// <summary>The <c>param5</c> it was cast with: its lifetime extension and its line offset.</summary>
        public readonly int Spacing;

        int _life;

        public MagSymbol(Vector2 positionAs3Pixels, string spellId, int spacing)
        {
            PositionAs3Pixels = positionAs3Pixels;
            SpellId = spellId;
            Spacing = spacing;
            _life = MagSymbolRules.Lifetime(spacing);
        }

        /// <summary>Ticks remaining. Starts at <c>20 + Spacing</c>.</summary>
        public int Life => _life;

        /// <summary>True once it has run out and the owner may drop it.</summary>
        public bool Expired => _life <= 0;

        /// <summary>
        /// Advance one tick, in the oracle's order: test the emit, decrement, test the apply, test the
        /// expiry. Returns every beat that happened so the caller can act on all of them.
        /// </summary>
        public MagSymbolStep Step()
        {
            MagSymbolStep step = MagSymbolStep.None;

            if (_life == MagSymbolRules.EmitAtLife)
            {
                step |= MagSymbolStep.Emitted;
            }

            _life--;

            if (_life == MagSymbolRules.AppliesAtLife)
            {
                step |= MagSymbolStep.Applied;
            }

            if (_life <= 0)
            {
                step |= MagSymbolStep.Expired;
            }

            return step;
        }
    }
}
