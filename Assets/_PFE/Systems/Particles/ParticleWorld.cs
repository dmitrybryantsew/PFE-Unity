using System;
using System.Collections.Generic;
using PFE.Core;
using PFE.Core.Rng;

namespace PFE.Systems.Particles
{
    /// <summary>
    /// The live particle population — the port of AS3's <c>fe.graph.Emitter</c> as a <i>world</i>: the row
    /// table, the two budgets, the pool of live parts, and the once-per-tick step.
    ///
    /// <para><b>Unity-free on purpose, and it is the point of the whole split.</b> Every collaborator here
    /// is an <c>int</c>-or-<c>float</c> seam (<see cref="IParticleSpriteFrames"/>,
    /// <see cref="IParticleTileWater"/>) or already Unity-free (<see cref="ParticleRules"/>,
    /// <see cref="ParticleBudget"/>, <see cref="IRngService"/>). So the interesting behaviour — the
    /// budget refusals, the spawn-delay gate, expiry, unknown ids — is all reachable from a plain host and
    /// can be asserted on offline. The sprite-bearing catalogue and the renderer are the thin adapters on
    /// the other side.</para>
    ///
    /// <para><b>The tick shape, and why the order matters.</b> One call to <see cref="SimTick"/> does
    /// <see cref="BeginTick"/> and then <see cref="Step"/>, in that order, and it runs <b>before</b> the
    /// systems that emit (see <c>SimTickOrder.PreTick</c>):</para>
    /// <list type="number">
    /// <item><description><b><see cref="BeginTick"/></b> is AS3's <c>kol2 = kol1; kol1 = 0;</c>
    /// (<c>World.as:1266-1267</c>). It rolls the previous tick's step count into the value the global
    /// ceiling reads, so an emitter later in this tick compares against <i>last</i> tick's population and
    /// cannot make a burst legal by draining the counter mid-tick.</description></item>
    /// <item><description><b><see cref="Step"/></b> advances and ages every live part, which is what
    /// fills <c>kol1</c> for the next tick's roll.</description></item>
    /// </list>
    ///
    /// <para>Running this <i>before</i> the emitters is not an optimisation — it is the oracle's
    /// behaviour. In AS3 a <c>Part</c> is a MovieClip and its <c>ENTER_FRAME</c> listener fires on the
    /// <b>next</b> frame, so a part cast during a tick is not stepped until the following one. A port that
    /// stepped after the emitters would move every fresh particle one tick early, and the only symptom
    /// would be a burst that looks very slightly too fast.</para>
    ///
    /// <para><b>A tickable that does not know the loop.</b> It is the tickable — <see cref="SimTick"/> is
    /// the whole step — but it takes no <c>SimLoop</c> reference, because <c>SimLoop</c>'s constructor
    /// needs a <see cref="PfeDebugSettings"/>, which is a <c>ScriptableObject</c> and therefore not
    /// constructible from an offline host. Injecting the loop here would make this class uninstantiable in
    /// the fixtures that are the reason it is Unity-free. <see cref="SimTickRegistrar"/> owns the loop
    /// reference and attaches every <see cref="IAutoRegisteredSimTickable"/> instead — which is also why
    /// there is no per-system driver class any more.</para>
    /// </summary>
    public sealed class ParticleWorld : IParticleWorld, IAutoRegisteredSimTickable
    {
        private readonly ParticleDefinitionTable _definitions;
        private readonly IParticleSpriteFrames _frames;
        private readonly IParticleTileWater _tiles;
        private readonly IRngService _rng;
        private readonly ParticleBudget _budget = new ParticleBudget();
        private readonly List<ParticleState> _live = new List<ParticleState>();
        private readonly HashSet<string> _unknown = new HashSet<string>();
        private readonly List<string> _unknownOrder = new List<string>();

        /// <summary>
        /// Wires the world.
        /// </summary>
        /// <param name="definitions">The 118 parsed rows. An empty table is legal — every id then reads as unknown.</param>
        /// <param name="frames">Frame counts for the spawn-time random-frame pick. Never null; pass
        /// <see cref="NullParticleSpriteFrames.Instance"/> when there is no art.</param>
        /// <param name="tiles">The water query for <c>water=</c> rows. Never null; pass
        /// <see cref="DryParticleTileWater.Instance"/> when there is no map.</param>
        /// <param name="rng">
        /// The jitter source. Salted onto <see cref="RngStream.Presentation"/>, because the oracle draws
        /// particle jitter from <c>Math.random()</c> — unseeded, presentation-only, and documented as
        /// allowed to be so. Keeping it off the combat stream means FX jitter can never shift a damage
        /// roll.
        /// </param>
        public ParticleWorld(
            ParticleDefinitionTable definitions,
            IParticleSpriteFrames frames,
            IParticleTileWater tiles,
            IRngService rng)
        {
            _definitions = definitions ?? ParticleDefinitionTable.Empty;
            _frames = frames ?? NullParticleSpriteFrames.Instance;
            _tiles = tiles ?? DryParticleTileWater.Instance;

            // Thrown rather than defaulted: a missing RNG is a container wiring bug, and a world that
            // silently stopped drawing jitter would look like a data problem instead of a DI one.
            if (rng == null) throw new ArgumentNullException(nameof(rng));
            _rng = rng.GetStream(RngStream.Presentation);
        }

        /// <summary>The row table, so a renderer can turn a <c>DefinitionIndex</c> back into a definition.</summary>
        public ParticleDefinitionTable Definitions => _definitions;

        /// <summary>The two budgets, including their drop counters — the readback for "why did nothing appear".</summary>
        public ParticleBudget Budget => _budget;

        /// <summary>
        /// The live parts, in spawn order (oldest first), with dead ones already removed. This is the
        /// renderer's input; it is the same list instance every tick, so a renderer must not retain it.
        /// </summary>
        public IReadOnlyList<ParticleState> Live => _live;

        /// <summary>How many parts are alive right now.</summary>
        public int LiveCount => _live.Count;

        /// <summary>Parts actually created since <see cref="Reset"/>. A refusal does not count.</summary>
        public int SpawnedTotal { get; private set; }

        /// <summary>Emit calls whose id did not resolve, since <see cref="Reset"/>.</summary>
        public int UnknownEmitTotal { get; private set; }

        /// <inheritdoc />
        public IReadOnlyCollection<string> UnknownIds => _unknownOrder;

        /// <inheritdoc />
        public bool Has(string id) => !string.IsNullOrEmpty(id) && _definitions.TryGet(id, out _);

        /// <inheritdoc />
        public bool Emit(string id, float x, float y) => Emit(id, x, y, null);

        /// <inheritdoc />
        /// <remarks>
        /// <b>False is not an error.</b> An unknown id and a budget refusal both return false and both are
        /// distinguishable: the first lands in <see cref="UnknownIds"/>, the second in
        /// <see cref="ParticleBudget.DroppedByGlobalBudget"/> / <see cref="ParticleBudget.DroppedByTypeBudget"/>.
        /// That split is deliberate — the oracle's <c>trace</c> is a console line nobody reads, and a
        /// dropped spawn that looks like a definition which emits nothing is this project's recurring
        /// failure.
        /// </remarks>
        public bool Emit(string id, float x, float y, ParticleSpec overrides)
        {
            if (string.IsNullOrEmpty(id)) return false;

            if (!_definitions.TryGet(id, out ParticleDefinition definition) || definition == null)
            {
                NoteUnknown(id);
                return false;
            }

            int spawned = ParticleRules.Cast(
                definition,
                _definitions.IndexOf(id),
                overrides ?? ParticleSpec.None,
                x,
                y,
                _rng,
                _budget,
                _live,
                _frames.FrameCount(definition));

            SpawnedTotal += spawned;
            return spawned > 0;
        }

        /// <inheritdoc />
        public void BeginTick() => _budget.BeginTick();

        /// <summary>
        /// One authoritative tick: roll the budget register, then advance the population. See the class
        /// remarks for why that order — and why it must sit at <see cref="SimTickOrder.PreTick"/>.
        /// </summary>
        /// <param name="tickIndex">
        /// Unused. Particles are pure presentation: nothing here reads the tick index, and the jitter is
        /// drawn from the unseeded <see cref="RngStream.Presentation"/> stream, so the population is
        /// deliberately <i>not</i> part of the deterministic state a peer must reproduce.
        /// </param>
        public void SimTick(int tickIndex)
        {
            BeginTick();
            Step();
        }

        /// <inheritdoc />
        public int TickOrder => SimTickOrder.PreTick;

        /// <summary>
        /// Advances every live part by one tick and drops the ones that died — AS3's <c>Part.step</c> run
        /// across the population, plus <c>setNull</c> for the ones that expired.
        ///
        /// <para>Iterated <b>backwards</b> so a removal cannot skip its neighbour. <see cref="ParticleRules.Step"/>
        /// releases the budget slot on expiry but deliberately leaves the state intact, so the last frame's
        /// position and rotation survive long enough to be drawn — the pool slot is this method's to free,
        /// not the rules'.</para>
        ///
        /// <para>Order is preserved: a part keeps its spawn position in the list until it dies, which is
        /// what makes the renderer's draw order match the oracle's <c>addChild</c> order within a
        /// <c>sloy</c> band.</para>
        /// </summary>
        public void Step()
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                ParticleState state = _live[i];

                // Only ask the map when the row cares: 112 of the 118 rows have water=0, and the query is
                // the only reason a tile lookup would be needed at all.
                int water = state.Water > 0 ? _tiles.WaterAt(state.X, state.Y) : 0;

                ParticleStepResult result = ParticleRules.Step(ref state, water, _budget);

                if (result == ParticleStepResult.Expired) _live.RemoveAt(i);
                else _live[i] = state;
            }
        }

        /// <summary>
        /// Drops every part and zeroes both budgets and every counter — AS3's <c>Grafon.clearAll</c>
        /// (<c>Grafon.as:709-712</c> re-creates each of the six <c>visObjs</c> layers), which is what a room
        /// transition does.
        ///
        /// <para><b>Distinct from <see cref="ClearUnknownIds"/>, and both are needed.</b> A room change must
        /// forget the particles but should <i>not</i> forget which ids failed to resolve — otherwise a
        /// typo'd id reported once becomes reported again after every transition, and the report loses its
        /// meaning as "this id has never worked".</para>
        /// </summary>
        public void Reset()
        {
            _live.Clear();
            _budget.Reset();
            SpawnedTotal = 0;
            UnknownEmitTotal = 0;
        }

        /// <summary>Forgets the unknown-id report. For a fresh session, not for a room change.</summary>
        public void ClearUnknownIds()
        {
            _unknown.Clear();
            _unknownOrder.Clear();
            UnknownEmitTotal = 0;
        }

        private void NoteUnknown(string id)
        {
            UnknownEmitTotal++;
            if (_unknown.Add(id)) _unknownOrder.Add(id);
        }
    }
}
