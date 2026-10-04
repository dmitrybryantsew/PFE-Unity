namespace PFE.Systems.Particles
{
    /// <summary>
    /// AS3 <c>Emitter.fils</c> (<c>Emitter.as:18-21</c>) — the two glow presets a row can name through
    /// <c>filter</c>. There are exactly two, and both are <c>GlowFilter</c>s:
    /// <c>bur</c> = <c>new GlowFilter(16742144, 1, 8, 8, 1, 1)</c> (orange), <c>plav</c> =
    /// <c>new GlowFilter(65280, 1, 8, 8, 1, 1)</c> (green).
    /// </summary>
    public enum ParticleFilter
    {
        /// <summary>No glow filter — 116 of the 118 rows.</summary>
        None = 0,

        /// <summary><c>bur</c> — orange glow, <c>0xFF8000</c>.</summary>
        Bur = 1,

        /// <summary><c>plav</c> — green glow, <c>0x00FF00</c>.</summary>
        Plav = 2
    }

    /// <summary>
    /// The <c>blend</c> attribute resolved to an enum. The oracle assigns the raw string straight to
    /// <c>DisplayObject.blendMode</c> (<c>Emitter.as:327</c>); the port maps it to a value the renderer
    /// can switch on. The shipped data uses four values plus the default — <c>screen</c> (25 rows),
    /// <c>overlay</c> (6), <c>multiply</c> (3), <c>hardlight</c> (3), and <c>normal</c> for the rest.
    /// </summary>
    public enum ParticleBlend
    {
        /// <summary>AS3 <c>"normal"</c> — the declaration's default (<c>Emitter.as:51</c>).</summary>
        Normal = 0,

        /// <summary>AS3 <c>"screen"</c>.</summary>
        Screen = 1,

        /// <summary>AS3 <c>"overlay"</c>.</summary>
        Overlay = 2,

        /// <summary>AS3 <c>"multiply"</c>.</summary>
        Multiply = 3,

        /// <summary>AS3 <c>"hardlight"</c>.</summary>
        HardLight = 4
    }

    /// <summary>
    /// A spawn request — the port of <c>Emitter.cast</c>'s duck-typed <c>param4</c> object
    /// (<c>Emitter.as:154-393</c>). One of these is built per <c>Emit</c> call and handed to
    /// <see cref="ParticleRules.Cast"/>.
    ///
    /// <para><b>The zero-means-absent asymmetry is the oracle's, and it is preserved exactly.</b> AS3
    /// tests these fields for <i>truthiness</i>, so a field that is present but zero is treated as
    /// absent. That is visible and occasionally surprising:</para>
    /// <list type="bullet">
    /// <item><description><c>kol</c>, <c>rx</c>, <c>ry</c>, <c>dx</c>, <c>dy</c>, <c>dr</c>,
    /// <c>frame</c>, <c>dframe</c>, <c>otklad</c> — <c>if (param4.x)</c>, so <b>0 means "not
    /// supplied"</b>. A caller cannot ask for a zero rotation delta; it gets the definition's.</description></item>
    /// <item><description><c>alpha</c>, <c>scale</c>, <c>rotation</c> — also truthy
    /// (<c>:315-326</c>), so <b><c>Alpha = 0</c> does nothing</b>. This is why
    /// <see cref="ParticleDefinition.PreAlph"/> exists as the way to spawn invisible; if you want a
    /// particle hidden at spawn, set <c>prealph</c> on the row, do not pass <c>Alpha = 0</c>.</description></item>
    /// <item><description><c>md</c> is the exception: <c>if (param4.md != null)</c> (<c>:254</c>), so a
    /// supplied <b>0 is honoured</b> — and zeroes the velocity, which is the only way to stop a
    /// definition's burst. Modelled as nullable for exactly that reason.</description></item>
    /// </list>
    ///
    /// <para><b>Deliberately absent: <c>txt</c>.</b> <c>numb</c>, <c>replic</c>/<c>replic2</c> and
    /// <c>gui</c>/<c>take</c> carry text and render a text field, not frames (<c>Emitter.as:367-379</c>).
    /// They belong with the HUD, not the particle system, and adding a text field here would imply this
    /// type can drive them.</para>
    ///
    /// <para><b>Deliberately absent: the spawn position.</b> AS3's <c>param4</c> never carries one —
    /// <c>cast</c> takes <c>x</c> and <c>y</c> as its own arguments (<c>Emitter.as:154</c>). Keeping
    /// them out of this type means there is exactly one place a position can come from, so a caller
    /// cannot set one on the spec and then pass a different one.</para>
    /// </summary>
    public sealed class ParticleSpec
    {
        /// <summary>A spec that supplies nothing — every override falls back to the definition.</summary>
        public static ParticleSpec None { get; } = new ParticleSpec();

        /// <summary>
        /// AS3 <c>param4.kol</c> — how many particles this emit spawns. Capped at 50 by the rules
        /// (<c>Emitter.as:176-179</c>), <b>after</b> the params merge. 0 means "absent", i.e. 1.
        /// </summary>
        public int Kol = 1;

        /// <summary>
        /// AS3 <c>param4.frame</c> — the 1-based start frame, or 0 for "pick one at random"
        /// (<c>Emitter.as:114</c>). 0 means absent, which is also the value that means random, so the two
        /// cases coincide — that is the oracle's shape, not a simplification.
        /// </summary>
        public int Frame;

        /// <summary>
        /// AS3 <c>param4.dframe</c> — when non-zero, the start frame is jittered by
        /// <c>floor(rand() * dframe + 1)</c> (<c>Emitter.as:307</c>). 0 means absent.
        /// </summary>
        public int DFrame;

        /// <summary>AS3 <c>param4.otklad</c> — overrides the definition's spawn delay. 0 means absent.</summary>
        public int Otklad;

        /// <summary>AS3 <c>param4.rx</c> — extra spawn-position jitter on X. 0 means absent.</summary>
        public float RX;

        /// <summary>AS3 <c>param4.ry</c> — extra spawn-position jitter on Y. 0 means absent.</summary>
        public float RY;

        /// <summary>AS3 <c>param4.dx</c> — extra velocity on X, added after the definition's own. 0 means absent.</summary>
        public float DX;

        /// <summary>AS3 <c>param4.dy</c> — extra velocity on Y. 0 means absent.</summary>
        public float DY;

        /// <summary>AS3 <c>param4.dr</c> — extra spin. 0 means absent.</summary>
        public float DR;

        /// <summary>
        /// AS3 <c>param4.md</c> — a multiplier applied to <b>both</b> velocity components after every
        /// other contribution (<c>Emitter.as:254-258</c>). Null means absent; a supplied 0 is honoured
        /// and kills the velocity outright.
        /// </summary>
        public float? Md;

        /// <summary>AS3 <c>param4.alpha</c> — start alpha. Null or 0 means absent (see the type doc).</summary>
        public float? Alpha;

        /// <summary>AS3 <c>param4.scale</c> — uniform start scale. Null or 0 means absent.</summary>
        public float? Scale;

        /// <summary>AS3 <c>param4.rotation</c> — start rotation in degrees, overriding the definition's. Null or 0 means absent.</summary>
        public float? Rotation;

        /// <summary>AS3 <c>param4.mirr</c> — mirror horizontally (<c>Emitter.as:358-361</c>).</summary>
        public bool Mirr;

        /// <summary>
        /// AS3 <c>param4.celx</c> / <c>param4.cely</c> — a target point. When both are supplied
        /// <b>and</b> the visual has a <c>len</c> child, the oracle stretches and aims that child from
        /// the particle to the target (<c>Emitter.as:344-357</c>). Null means absent.
        /// </summary>
        public float? CelX;

        /// <inheritdoc cref="CelX"/>
        public float? CelY;
    }

    /// <summary>
    /// One live particle — the port of AS3's <c>fe.graph.Part</c> (<c>Part.as</c>).
    ///
    /// <para><b>A mutable struct, on purpose.</b> The oracle spawns up to 50 per <c>cast</c> with a
    /// 12-per-type cap and a global <c>maxParts</c> of 100; the obvious first cut — a GameObject per
    /// particle — is the framerate. Particles live in a pooled array of these, so every field must be a
    /// value type and the type must carry no reference to a <c>ParticleDefinition</c>; the definition is
    /// reached through <see cref="DefinitionIndex"/>.</para>
    ///
    /// <para><b>Not carried: the visual.</b> AS3's <c>Part</c> owns a <c>vis</c> MovieClip or a
    /// <c>BitmapData</c> blit cell. The port's state carries only the numbers; the renderer resolves
    /// sprites from the definition at draw time. That is what keeps this type off the <c>ECall</c>
    /// wall — the whole of <see cref="ParticleRules"/> can then run in a plain host.</para>
    /// </summary>
    public struct ParticleState
    {
        /// <summary>
        /// Row index into the <see cref="ParticleDefinitionTable"/>, or <b>-1</b> when this slot is
        /// free. The pool's liveness flag is this field and nothing else — a separate bool would be a
        /// second source of truth for the same fact.
        /// </summary>
        public int DefinitionIndex;

        // ── Position and motion ───────────────────────────────────────────────

        /// <summary>
        /// Position in <b>AS3 room-local pixels</b> — the space <c>Emitter.emit</c> is called in.
        /// </summary>
        /// <remarks>
        /// <para><b>AS3 space, not Unity space, and Y is the trap.</b> AS3 runs Y <i>downward</i> with
        /// tile row 0 at the ceiling; the port's room-local Unity space runs <i>upward</i> with row 0 at
        /// the floor. So every Unity-side consumer has to mirror Y —
        /// <c>ParticleRenderer</c> via <c>WorldCoordinates.As3YToUnityLocalY</c>,
        /// <c>TileQueryParticleWater</c> via <c>WorldCoordinates.As3YToUnityRow</c>. X needs no mirror;
        /// both spaces run left-to-right.</para>
        /// <para><b>The rules stay in AS3 space deliberately.</b> <c>ParticleRules</c> integrates AS3's
        /// <c>Y += ddy</c> — downward gravity. Flipping the axis here instead would mean negating the
        /// gravity term, which is an invented divergence from the oracle; the mirror belongs at the Unity
        /// boundary, where it is also reachable from a fixture.</para>
        /// <para>These two fields carried no documentation at all until a review found that the renderer
        /// and the water adapter had each guessed a different space. The contract is stated once, here.</para>
        /// </remarks>
        public float X;

        /// <summary>See <see cref="X"/> — AS3 room-local pixels, Y downward.</summary>
        public float Y;
        public float DX;
        public float DY;
        public float DDY;
        public float Brake;
        public float R;
        public float DR;

        /// <summary>
        /// AS3 <c>Part.isMove</c>, computed once at spawn from the velocities (<c>Emitter.as:282</c>)
        /// and never recomputed. <c>Part.step</c> integrates <b>only</b> when this is true, so a particle
        /// with no velocity, no jitter and no gravity is frozen at its spawn point for its whole life —
        /// it still fades and still dies, it just does not move.
        /// </summary>
        public bool IsMove;

        // ── Life ──────────────────────────────────────────────────────────────

        public int Liv;
        public int MLiv;

        // ── Appearance ────────────────────────────────────────────────────────

        public bool IsAlph;
        public bool IsPreAlph;
        public float Alpha;
        public float Scale;

        /// <summary>Layer index — AS3 <c>Pt.sloy</c> (<c>Pt.as:23</c>), an <c>int</c> selecting one of the six <c>Grafon.visObjs</c> layers.</summary>
        public int Sloy;

        public bool Mirr;
        public bool Ctrans;
        public ParticleBlend Blend;
        public ParticleFilter Filter;

        // ── Animation ─────────────────────────────────────────────────────────

        /// <summary>AS3 <c>anim</c> copied from the definition: 0 = freeze, 1 = play from a frame, 2 = play from a random frame.</summary>
        public int Anim;

        /// <summary>
        /// Which of the two visual paths this particle came from — the <c>blit=</c> sheet (true) or the
        /// <c>vis=</c> class (false). The two advance their frames differently (<c>Part.as:183-191</c>
        /// versus <c>:126-133</c>), and the definition itself is not carried on the state, so the choice
        /// has to be recorded here at spawn.
        /// </summary>
        public bool IsBlit;

        /// <summary>
        /// <b>The frame the renderer must draw this tick</b>, 0-based. Set by
        /// <see cref="ParticleRules.Step"/> and by the spawn, and valid for both the <c>vis=</c> and
        /// <c>blit=</c> paths — so the renderer never has to know which one it is holding, and never has
        /// to advance anything itself.
        ///
        /// <para>It is a <b>captured</b> value, not a cursor. AS3's <c>Part.step</c> draws and then
        /// advances within the same call (<c>Part.as:183-191</c>); the port splits draw from advance, so
        /// the rules capture the pre-advance frame here before moving the cursor on. Drawing the cursor
        /// after <c>Step</c> instead would show every blit one frame ahead of the oracle.</para>
        /// </summary>
        public int DrawFrame;

        /// <summary>
        /// Cursor for the <c>vis=</c> path — the next frame to draw. AS3 frames are 1-based and the
        /// port's sprite arrays are 0-based, so this is the AS3 frame minus one throughout.
        /// </summary>
        public int FrameIndex;

        /// <summary>Total frames available in the visual — AS3 <c>vis.totalFrames</c>, or the blit sheet's <c>width / blitX</c>.</summary>
        public int FrameCount;

        /// <summary>Fractional frame cursor for the <c>blit=</c> path (<c>Part.blitFrame</c>); the drawn frame is <c>floor</c> of it.</summary>
        public float BlitFrame;

        /// <summary>AS3 <c>blitDelta</c> — per-tick advance of <see cref="BlitFrame"/>.</summary>
        public float BlitDelta;

        /// <summary>
        /// AS3 <c>blitMFrame</c> — the wrap length for a repeating blit animation. <c>&lt;= 0</c> means no
        /// wrap, which is what the oracle's <c>blitMFrame &gt; 0</c> guard encodes.
        /// </summary>
        public int BlitLoopFrames;

        // ── Spawn delay ───────────────────────────────────────────────────────

        /// <summary>AS3 <c>Part.otklad</c> — ticks still to wait, invisible, before the particle starts.</summary>
        public int Otklad;

        /// <summary>AS3 <c>Part.vis.visible</c> — cleared while <see cref="Otklad"/> is counting down.</summary>
        public bool Visible;

        // ── Environment / budget ──────────────────────────────────────────────

        /// <summary>AS3 <c>Part.water</c> — 0 = anywhere, 1 = only outside water, 2 = only in water.</summary>
        public int Water;

        /// <summary>AS3 <c>Part.maxkol</c> — the budget slot this particle occupies, or 0 for none.</summary>
        public int MaxKol;

        /// <summary>True when this slot holds a live particle.</summary>
        public bool IsAlive => DefinitionIndex >= 0;

        /// <summary>Clears the slot. The definition index is the liveness flag, so this is the whole reset.</summary>
        public void Clear() => DefinitionIndex = -1;
    }
}
