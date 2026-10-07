using System;
using System.Collections.Generic;

namespace PFE.Systems.Particles
{
    /// <summary>
    /// One <c>&lt;part&gt;</c> row from <c>AllData.as</c> — the port of AS3's <c>fe.graph.Emitter</c>
    /// field set (<c>Emitter.as:23-101</c>), resolved to effective values.
    ///
    /// <para><b>This is a resolved row, not a raw one.</b> The oracle's <c>Emitter(param1)</c>
    /// (<c>Emitter.as:103-127</c>) starts from the field defaults and overwrites each one that the XML
    /// happens to carry, so an absent attribute means "the default", never "unset". The reader
    /// (<see cref="ParticleXmlReader"/>) reproduces that, which is why every field here has a default
    /// equal to the AS3 declaration's initialiser rather than <c>0</c>.</para>
    ///
    /// <para><b>Naming.</b> PascalCase, unlike the camelCase used for Unity-serialised data in
    /// <c>Data/Definitions</c>. This type is runtime data owned by a system, not an Inspector-facing
    /// asset, and its consumers are the pure rules; the case is a marker for which side of the split a
    /// type lives on.</para>
    ///
    /// <para><b>Three AS3 fields are deliberately absent</b>, because no <c>&lt;part&gt;</c> row can
    /// ever reach them and reproducing them would be a dead seam:</para>
    /// <list type="bullet">
    /// <item><description><c>frame</c> / <c>dframe</c> — <c>cast</c> opens with
    /// <c>this.frame = this.dframe = 0;</c> (<c>Emitter.as:180</c>) and then only ever writes them from
    /// the spawn params, so the XML attributes are overwritten before any read. No row sets either. They
    /// live on <see cref="ParticleSpec"/> instead, which is the only thing that can populate them.</description></item>
    /// <item><description><c>move</c> — declared (<c>:65</c>) and never read; <c>Part.isMove</c> is
    /// computed in <c>cast</c> from the velocities (<c>:282</c>). No row sets it either
    /// (<c>grep -c "move='" AllData.as</c> = 0).</description></item>
    /// <item><description><c>visClass</c> — the resolved <c>Class</c> object; the port resolves the
    /// visual from <see cref="Vis"/> / <see cref="Blit"/> through the sprite catalog at render time, on
    /// the Unity side, where it belongs.</description></item>
    /// </list>
    ///
    /// <para><b>Attributes the data carries that this type does not</b> — see
    /// <see cref="ParticleXmlReader.IgnoredAttributes"/>. <c>rr</c> (7 rows) and <c>rd</c> (3 rows) are
    /// written in the data, and <c>rr</c> is even <i>documented</i> in the block's own comment
    /// (<c>AllData.as:6884</c>: <c>rr - случайная скорость вращения</c>), but <c>Emitter</c> declares no
    /// field with either name, so <c>hasOwnProperty</c> rejects them and the oracle silently drops them.
    /// The port drops them too — and reports them, which the oracle does not.</para>
    /// </summary>
    [Serializable]
    public class ParticleDefinition
    {
        // ── Identity ──────────────────────────────────────────────────────────

        /// <summary>AS3 <c>&lt;part id&gt;</c> — the key <c>Emitter.arr</c> is built on.</summary>
        public string Id;

        // ── What it draws ─────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>vis</c> — a <c>visual*</c> MovieClip class name from <c>assets.swf</c>. 94 of the 118
        /// rows use this path. Mutually exclusive with <see cref="Blit"/>: no row sets both, and no row
        /// sets neither (verified over all 118).
        /// </summary>
        public string Vis;

        /// <summary>
        /// AS3 <c>blit</c> — a <c>spr*</c> sprite sheet name, blitted frame by frame. 24 of the 118 rows
        /// use this path. The sheets are <b>not</b> in <c>assets.swf</c>; they come from
        /// <c>sprite.swf</c> (see <c>Grafon.texUrl</c>, <c>Grafon.as:27</c>).
        /// </summary>
        public string Blit;

        /// <summary>AS3 <c>blitx</c> — blit cell width. 0 means the oracle's <c>Part</c> default of 120 applies.</summary>
        public int BlitX;

        /// <summary>AS3 <c>blity</c> — blit cell height. 0 means the oracle's <c>Part</c> default of 120 applies.</summary>
        public int BlitY;

        /// <summary>
        /// AS3 <c>blitf</c> — the length in frames of the <b>repeating</b> part of a blit animation, not
        /// the sheet size. The data comment calls it "количество кадров в повторяющейся анимации". Only
        /// <c>fire</c> sets it (<c>17</c>, against a 32-frame <c>sprFire</c> sheet). <c>-1</c> means
        /// "no loop" — the AS3 declaration's own value (<c>Emitter.as:39</c>), which is why the guard in
        /// <c>cast</c> is <c>&gt; 0</c> rather than <c>!= 0</c>.
        /// </summary>
        public int BlitLoopFrames = -1;

        /// <summary>AS3 <c>blitd</c> — blit frame advance per tick. Default 1.</summary>
        public float BlitDelta = 1f;

        /// <summary>AS3 <c>anim</c> — 0 = freeze on a frame, 1 = play from <c>frame+1</c>, 2 = play from a random frame.</summary>
        public int Anim;

        // ── Where it sits in the draw order ───────────────────────────────────

        /// <summary>
        /// AS3 <c>sloy</c> — the layer index, default 3. <c>Emitter.sloy</c> is declared <c>*</c>
        /// (<c>Emitter.as:29</c>) so it holds the raw XML attribute node and is coerced on assignment to
        /// <c>Pt.sloy</c>, which is an <c>int</c> (<c>Pt.as:23</c>). The value indexes
        /// <c>Grafon.visObjs</c>, the six stacked <c>Sprite</c> layers a room's visuals are drawn into
        /// (<c>Grafon.as:184-202</c>); higher is nearer the front. The data uses 1..5.
        /// </summary>
        public int Sloy = 3;

        // ── Budget ────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>imp</c> — "important". A truthy value <b>bypasses the global part budget</b>
        /// (<c>Emitter.as:167</c>). 36 of the 118 rows set it. This is not decoration: it is the
        /// difference between "the explosion renders" and "the explosion silently vanishes once the room
        /// is busy". <see cref="ParticleBudget"/> reproduces it, and the port gives it a readback.
        /// </summary>
        public int Imp;

        /// <summary>
        /// AS3 <c>maxkol</c> — per-type concurrency cap slot. When non-zero, <c>cast</c> stops
        /// mid-batch once <c>kols[maxkol]</c> reaches 12 (<c>Emitter.as:184</c>). Only 3 rows set it, all
        /// to <c>1</c>. The oracle's <c>kols</c> array has 6 slots, so 0..5 is the addressable range.
        /// </summary>
        public int MaxKol;

        // ── Lifetime ──────────────────────────────────────────────────────────

        /// <summary>AS3 <c>minliv</c> — minimum life in ticks. Every row sets it; default 20.</summary>
        public int MinLiv = 20;

        /// <summary>AS3 <c>rliv</c> — life jitter: <c>life = minliv + floor(rand() * rliv)</c>.</summary>
        public int RLiv;

        // ── Velocity at spawn ─────────────────────────────────────────────────

        /// <summary>AS3 <c>minv</c> — minimum speed, applied at a random angle. Only used when <c>minv + rv &gt; 0</c>.</summary>
        public float MinV;

        /// <summary>AS3 <c>rv</c> — speed jitter: <c>speed = minv + rand() * rv</c>.</summary>
        public float RV;

        /// <summary>AS3 <c>rx</c> — spawn-position jitter on X: <c>(rand() - 0.5) * rx</c>.</summary>
        public float RX;

        /// <summary>AS3 <c>ry</c> — spawn-position jitter on Y.</summary>
        public float RY;

        /// <summary>AS3 <c>rdx</c> — random velocity on X: <c>(rand() - 0.5) * rdx</c>, added after the angle burst.</summary>
        public float RDX;

        /// <summary>AS3 <c>rdy</c> — random velocity on Y.</summary>
        public float RDY;

        /// <summary>AS3 <c>dx</c> — a <b>fixed</b> velocity bias on X. <b>No row in the data sets this</b> (see the reader's collision note); it exists for completeness and reads 0 everywhere.</summary>
        public float DX;

        /// <summary>AS3 <c>dy</c> — a fixed velocity bias on Y. 24 rows set it, nearly all to <c>-5</c> (debris arcs upward).</summary>
        public float DY;

        /// <summary>AS3 <c>rdr</c> — spin: <c>dr = (rand() - 0.5) * rdr</c>. Note the data's comment documents this as <c>rr</c>, a name <c>Emitter</c> does not have.</summary>
        public float RDR;

        /// <summary>AS3 <c>rot</c> — when non-zero, a random initial rotation in degrees: <c>r = rand() * 360</c>.</summary>
        public int Rot;

        /// <summary>AS3 <c>brake</c> — per-tick velocity damping multiplier. Default 1 (no damping).</summary>
        public float Brake = 1f;

        // ── Gravity ───────────────────────────────────────────────────────────

        /// <summary>AS3 <c>grav</c> — gravity susceptibility: <c>ddy = World.ddy * grav</c>.</summary>
        public float Grav;

        /// <summary>AS3 <c>rgrav</c> — gravity jitter: <c>ddy += World.ddy * rgrav * rand()</c>.</summary>
        public float RGrav;

        // ── Appearance ────────────────────────────────────────────────────────

        /// <summary>AS3 <c>scale</c> — uniform scale applied when != 1.</summary>
        public float Scale = 1f;

        /// <summary>AS3 <c>rsc</c> — random scale spread: <c>scale = scale - rsc + rand() * rsc</c>, i.e. uniform on <c>[scale-rsc, scale)</c>.</summary>
        public float Rsc;

        /// <summary>AS3 <c>alph</c> — fade out over the last 9 ticks of life.</summary>
        public bool Alph;

        /// <summary>AS3 <c>prealph</c> — start invisible and fade in over the first 9 ticks.</summary>
        public bool PreAlph;

        /// <summary>AS3 <c>ctrans</c> — inherit the <b>location's</b> colour transform. Not the unit's; <c>freezing</c> writes the unit's, and conflating the two is a known trap.</summary>
        public bool Ctrans;

        /// <summary>AS3 <c>blend</c> — blend mode name; default <c>"normal"</c>. See <see cref="ParticleBlend"/>.</summary>
        public string Blend = "normal";

        /// <summary>AS3 <c>filter</c> — a glow preset: <c>"bur"</c> (orange) or <c>"plav"</c> (green), the only two <c>Emitter.fils</c> defines. Null for the other 116 rows.</summary>
        public string Filter;

        /// <summary>AS3 <c>camscale</c> — scale by <c>1 / cam.scaleV</c>. Out of scope until the camera seam exists; carried so the row is not silently lossy.</summary>
        public bool CamScale;

        // ── Behaviour ─────────────────────────────────────────────────────────

        /// <summary>AS3 <c>water</c> — 0 = anywhere, 1 = only outside water, 2 = only in water. One row sets it.</summary>
        public int Water;

        /// <summary>
        /// AS3 <c>otklad</c> — spawn delay: <c>otklad = floor(rand() * otklad + 1)</c>, giving 1..otklad
        /// ticks of invisibility before the particle starts. One row sets it.
        /// </summary>
        public int Otklad;
    }

    /// <summary>
    /// The port of AS3's <c>Emitter.arr</c> (<c>Emitter.as:10</c>, built in <c>init()</c> at
    /// <c>:129-139</c>): every <c>&lt;part&gt;</c> row keyed by id.
    ///
    /// <para><b>Why an index rather than a <c>Dictionary&lt;string, ParticleDefinition&gt;</c> handed
    /// straight to the rules.</b> A live particle stores an <c>int</c> definition index, not a string or
    /// an object reference — it is a struct in a pooled array and must stay blittable and
    /// allocation-free. The table is the only place the id → index mapping lives.</para>
    ///
    /// <para><b>Unknown ids are a normal outcome, not an error.</b> The oracle <c>trace</c>s
    /// <c>"Нет частицы " + id</c> and carries on (<c>Emitter.as:150</c>); a typo must look like a missing
    /// visual, never like a crash. <see cref="TryGet"/> returns false and the caller decides — the
    /// runtime records the miss on the same "unmapped" surface the effect payloads already use.</para>
    /// </summary>
    public sealed class ParticleDefinitionTable
    {
        private readonly ParticleDefinition[] _definitions;
        private readonly Dictionary<string, int> _indexById;

        /// <summary>
        /// Builds a table. Later rows with a duplicate id <b>do not</b> replace earlier ones — the
        /// oracle's <c>arr[id] = ...</c> would, but the shipped data has no duplicate ids (verified over
        /// all 118), and silently keeping the first makes an accidental duplicate visible as a mismatch
        /// rather than invisible as a last-writer-wins.
        /// </summary>
        public ParticleDefinitionTable(IReadOnlyList<ParticleDefinition> definitions)
        {
            int count = definitions?.Count ?? 0;
            _definitions = new ParticleDefinition[count];
            _indexById = new Dictionary<string, int>(count, StringComparer.Ordinal);

            for (int i = 0; i < count; i++)
            {
                ParticleDefinition def = definitions[i];
                _definitions[i] = def;
                if (def == null || string.IsNullOrEmpty(def.Id)) continue;
                if (!_indexById.ContainsKey(def.Id)) _indexById.Add(def.Id, i);
            }
        }

        /// <summary>An empty table. Every lookup misses.</summary>
        public static ParticleDefinitionTable Empty { get; } =
            new ParticleDefinitionTable(Array.Empty<ParticleDefinition>());

        /// <summary>Number of rows, including any with a null or empty id.</summary>
        public int Count => _definitions.Length;

        /// <summary>The definition at <paramref name="index"/>, or null when out of range.</summary>
        public ParticleDefinition this[int index] =>
            index >= 0 && index < _definitions.Length ? _definitions[index] : null;

        /// <summary>Definition index for an id, or -1 when the id is not in the table.</summary>
        public int IndexOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            return _indexById.TryGetValue(id, out int index) ? index : -1;
        }

        /// <summary>True when the id resolves. The oracle's <c>if(_loc6_)</c> test at <c>Emitter.as:144</c>.</summary>
        public bool TryGet(string id, out ParticleDefinition definition)
        {
            int index = IndexOf(id);
            if (index < 0)
            {
                definition = null;
                return false;
            }
            definition = _definitions[index];
            return true;
        }

        /// <summary>Every id in the table, in row order. Useful for diagnostics and for the importer's report.</summary>
        public IReadOnlyList<ParticleDefinition> Definitions => _definitions;
    }
}
