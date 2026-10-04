namespace PFE.Systems.Particles
{
    /// <summary>
    /// The port of AS3's two particle budgets — the global <c>maxParts</c> ceiling and the per-type
    /// <c>kols[maxkol]</c> concurrency cap.
    ///
    /// <para><b>Both are mandatory, and neither is a soft hint.</b> A grep-and-spawn port that ignores
    /// them will spawn 50 parts per pellet per tick and hold them for their whole life. They are the
    /// reason a busy room stays playable, and they are also the reason an effect can appear to "not
    /// work":</para>
    /// <list type="bullet">
    /// <item><description><b>Global</b> — <c>if (kol2 &gt; World.w.maxParts &amp;&amp; this.imp == 0)
    /// return null;</c> (<c>Emitter.as:167</c>). Checked <b>once per <c>cast</c></b>, before the batch.
    /// Non-<c>imp</c> spawns are dropped <i>outright</i> once the world is full. <c>maxParts</c> is 100
    /// (<c>World.as:208</c>).</description></item>
    /// <item><description><b>Per-type</b> — <c>if (this.maxkol &gt; 0 &amp;&amp; kols[this.maxkol] &gt;=
    /// 12) return _loc6_;</c> (<c>Emitter.as:184</c>). Checked <b>per particle, inside the batch</b>, so
    /// a <c>kol=30</c> acid burst stops at 12 and returns early rather than rounding down.</description></item>
    /// </list>
    ///
    /// <para><b>Why <c>kol1</c>/<c>kol2</c> are a two-slot shift register.</b> <c>Part.step</c> ends with
    /// <c>++Emitter.kol1</c>, and once per world tick <c>World.as:1266-1267</c> runs
    /// <c>kol2 = kol1; kol1 = 0;</c>. So the gate at <c>:167</c> compares against the number of parts
    /// that were <b>live last tick</b>, not this one — a spawn burst cannot make itself legal by
    /// draining the counter mid-tick. <see cref="BeginTick"/> is that swap, and it must be called once
    /// per simulation tick, before anything emits.</para>
    ///
    /// <para><b>The drop counters are an addition.</b> The oracle drops silently. Because a dropped
    /// spawn is indistinguishable from a definition that emits nothing, the port counts both refusals —
    /// so "the explosion did not render" can be told apart from "the explosion was never emitted".</para>
    /// </summary>
    public sealed class ParticleBudget
    {
        /// <summary>AS3 <c>Emitter.kols</c> is <c>[0,0,0,0,0,0]</c> (<c>Emitter.as:12</c>) — six slots, so <c>maxkol</c> is addressable in 1..5.</summary>
        public const int MaxKolSlots = 6;

        /// <summary>The per-type cap, hard-coded in the oracle at <c>Emitter.as:184</c>.</summary>
        public const int TypeConcurrencyCap = 12;

        /// <summary>The per-<c>cast</c> spawn cap (<c>Emitter.as:176-179</c>).</summary>
        public const int CastCountCap = 50;

        /// <summary>AS3 <c>World.maxParts</c> (<c>World.as:208</c>).</summary>
        public const int DefaultMaxParts = 100;

        private readonly int[] _kols = new int[MaxKolSlots];
        private int _kol1;
        private int _kol2;

        /// <summary>The global live-particle ceiling. Defaults to the oracle's 100.</summary>
        public int MaxParts = DefaultMaxParts;

        /// <summary>Particles stepped since the last <see cref="BeginTick"/> — AS3 <c>Emitter.kol1</c>.</summary>
        public int Kol1 => _kol1;

        /// <summary>Particles stepped in the previous tick — AS3 <c>Emitter.kol2</c>, the value the global gate actually reads.</summary>
        public int Kol2 => _kol2;

        /// <summary>How many <c>cast</c> calls the global ceiling refused, since the last <see cref="Reset"/>.</summary>
        public int DroppedByGlobalBudget { get; private set; }

        /// <summary>How many individual particles the per-type cap refused, since the last <see cref="Reset"/>.</summary>
        public int DroppedByTypeBudget { get; private set; }

        /// <summary>
        /// The live count in a <c>maxkol</c> slot, or 0 when the slot is out of range. Mirrors the
        /// oracle's behaviour rather than throwing: <c>kols[7]</c> in AS3 is <c>undefined</c>, and
        /// <c>undefined &gt;= 12</c> is false, so an out-of-range slot silently means "no cap".
        /// </summary>
        public int LiveOfType(int maxKol) =>
            maxKol > 0 && maxKol < MaxKolSlots ? _kols[maxKol] : 0;

        /// <summary>
        /// The once-per-tick swap: <c>kol2 = kol1; kol1 = 0;</c> (<c>World.as:1266-1267</c>). Call it
        /// once per simulation tick, before any emitter runs.
        /// </summary>
        public void BeginTick()
        {
            _kol2 = _kol1;
            _kol1 = 0;
        }

        /// <summary>
        /// AS3 <c>if (kol2 &gt; World.w.maxParts &amp;&amp; this.imp == 0)</c> (<c>Emitter.as:167</c>).
        /// True when a <c>cast</c> of this definition is allowed to start at all.
        /// </summary>
        public bool AllowsGlobal(ParticleDefinition definition) =>
            !(_kol2 > MaxParts && definition.Imp == 0);

        /// <summary>
        /// AS3 <c>if (this.maxkol &gt; 0 &amp;&amp; kols[this.maxkol] &gt;= 12)</c>, negated
        /// (<c>Emitter.as:184</c>). True when this particle may be added to its type slot.
        /// </summary>
        public bool AllowsType(ParticleDefinition definition) =>
            definition.MaxKol <= 0 || definition.MaxKol >= MaxKolSlots
            || _kols[definition.MaxKol] < TypeConcurrencyCap;

        /// <summary>
        /// Claims a slot for a particle about to spawn — AS3 <c>if (maxkol &gt; 0) { ++kols[maxkol]; }</c>
        /// (<c>Emitter.as:201-205</c>). Returns the slot to hand to <see cref="Release"/>, or 0 for none.
        /// </summary>
        public int Reserve(ParticleDefinition definition)
        {
            int maxKol = definition.MaxKol;
            if (maxKol <= 0 || maxKol >= MaxKolSlots) return 0;
            _kols[maxKol]++;
            return maxKol;
        }

        /// <summary>
        /// Frees a slot when a particle dies — AS3 <c>if (maxkol &gt; 0) --Emitter.kols[maxkol];</c> in
        /// <c>Part.setNull</c> (<c>Part.as:73-76</c>). Guarded against underflow, which the oracle is not.
        /// </summary>
        public void Release(int maxKol)
        {
            if (maxKol <= 0 || maxKol >= MaxKolSlots) return;
            if (_kols[maxKol] > 0) _kols[maxKol]--;
        }

        /// <summary>
        /// Records that a particle was stepped — AS3 <c>++Emitter.kol1</c> at the end of
        /// <c>Part.step</c> (<c>Part.as:205</c>). A particle waiting out its <c>otklad</c> returns before
        /// this point and is <b>not</b> counted.
        /// </summary>
        public void CountStep() => _kol1++;

        /// <summary>Records a refused <c>cast</c>. Diagnostics only.</summary>
        public void NoteGlobalDrop() => DroppedByGlobalBudget++;

        /// <summary>Records a refused particle inside a batch. Diagnostics only.</summary>
        public void NoteTypeDrop() => DroppedByTypeBudget++;

        /// <summary>Zeroes every counter and every slot. For room transitions and for fixtures.</summary>
        public void Reset()
        {
            for (int i = 0; i < _kols.Length; i++) _kols[i] = 0;
            _kol1 = 0;
            _kol2 = 0;
            DroppedByGlobalBudget = 0;
            DroppedByTypeBudget = 0;
        }
    }
}
