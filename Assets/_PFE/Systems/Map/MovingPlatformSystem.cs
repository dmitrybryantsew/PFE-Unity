using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Map
{
    /// <summary>
    /// One object's moving-platform state — the port of the <c>isMove</c> half of AS3's
    /// <c>Interact</c> (<c>serv/Interact.as:130-152</c> fields, <c>:1823-1930</c> behaviour).
    ///
    /// <para><b>What it is not.</b> It carries no rider. AS3's <c>Box.bindMove</c> does forward to
    /// <c>this.un.bindMove(...)</c> (<c>Box.as:1235-1248</c>), but <c>un</c> is a
    /// <c>VirtualUnit</c> — a damage proxy installed by <c>Box.bindUnit</c> only when the definition
    /// authors <c>actdam</c> (<c>Box.as:410-419</c>) — and <c>VirtualUnit.damage</c> forwards straight
    /// back to <c>owner.command("dam")</c>. It is not a passenger. A platform in this game does not
    /// carry the player; the player's own motor resolves against it. That matters here because it means
    /// this type has nothing to do with <c>UnitController</c> and needs no rider bookkeeping.</para>
    /// </summary>
    public sealed class MovingPlatformState
    {
        /// <summary>The placed object this state drives.</summary>
        public readonly ObjectInstance Object;

        private readonly MapObjectMoveData _move;

        /// <summary>AS3 <c>isMove</c> — set whenever the placement authors a <c>move</c> node.</summary>
        public bool IsMove { get; }

        /// <summary>AS3 <c>begX</c> — the object's <c>X</c> at spawn.</summary>
        public float BegX { get; private set; }

        /// <summary>AS3 <c>begY</c> — the object's <c>Y</c> at spawn.</summary>
        public float BegY { get; private set; }

        /// <summary>AS3 <c>endX</c> — <c>X ± dx * tileX</c>, sign flipped when the room is mirrored.</summary>
        public float EndX { get; private set; }

        /// <summary>AS3 <c>endY</c> — <c>Y + dy * tileY</c>.</summary>
        public float EndY { get; private set; }

        /// <summary>
        /// AS3 <c>t_move</c> — frames elapsed inside the four-phase cycle. Distinct from
        /// <see cref="TravelFrames"/> (AS3 <c>tMove</c>), which is the duration of one leg; the two
        /// names differing by a single underscore in the oracle is why they are spelled out here.
        /// </summary>
        public float ElapsedFrames { get; private set; }

        /// <summary>AS3 <c>dt_move</c> — the per-step increment. Starts at 1 and only ever grows toward 1, so in the shipped data it is the constant 1.</summary>
        public float RampStep { get; private set; } = 1f;

        /// <summary>AS3 <c>tStay</c> — frames parked at each end.</summary>
        public int StayFrames { get; }

        /// <summary>AS3 <c>tMove</c> — frames spent travelling each way.</summary>
        public int TravelFrames { get; }

        /// <summary>
        /// AS3 <c>moveSt</c>. <c>0</c> = idle, <c>4</c> = run continuously; <c>1</c>/<c>2</c>/<c>3</c>
        /// are one-shot requests that clear themselves at the phase boundary that satisfies them.
        /// </summary>
        public int MoveSt { get; private set; }

        /// <summary>AS3 <c>moveP</c> — whether the last phase was a travelling one. Only used to decide which sound to play.</summary>
        public bool MoveP { get; private set; }

        /// <summary>Total length of one full cycle: stay, travel, stay, travel.</summary>
        private float CycleFrames => (StayFrames + TravelFrames) * 2f;

        public MovingPlatformState(ObjectInstance obj, in MapObjectMoveData move)
        {
            Object = obj;
            _move = move;
            IsMove = true;

            // BindMove writes `cdx`/`cdy` on this object's dynamic state, and a state that has never
            // been structured has no `dynamicState` to write to. `RoomObjectPhysicsLayer.Register`
            // normally does this first, but the platform system can be built for a room assembled by a
            // fixture, and a rider that reads nothing is the silent failure this whole type exists to
            // avoid. The call is memoised (RoomInstance._migratedFromParameters), so it is a field read
            // on every path that has already structured the object.
            obj.EnsureStructuredData();

            // AS3 reads X/Y off the object, which at this point are the placement's authored
            // coordinates (Interact.as:429-431). The port stores the same pair in `position`.
            BegX = obj.position.x;
            BegY = obj.position.y;

            // Interact.as:432-451. The `@dx`-absent arm writes `endX = endX2 = X`; the port has no
            // `endX2` (it is only ever assigned, never read in the oracle), so only endX is kept.
            // X is not mirrored between the two spaces, so `dx` keeps its sign.
            EndX = move.HasDx
                ? BegX + (move.Mirror ? -move.Dx : move.Dx) * WorldConstants.TILE_SIZE
                : BegX;

            // Interact.as:448-451 — `endY = Y + dy * tileY`. AS3's Y runs DOWN, the port's room-local
            // pixel Y runs UP (`WorldCoordinates.As3YToUnityLocalY` is the mirror), so the sign has to
            // flip or every vertical platform travels the wrong way: `RoomsSerial2.as:4103` authors
            // `dy="-19"` (rise 19 tiles) and the port would descend. Measured over the 640 imported
            // rooms: 45 `<move>` nodes author a `dy` — 19 negative, 26 positive — so both directions
            // occur and neither can be special-cased.
            EndY = move.HasDy
                ? BegY - move.Dy * WorldConstants.TILE_SIZE
                : BegY;

            // Interact.as:453-467. Note tStay/tMove were already defaulted to 10/100 by the data type,
            // matching the oracle's field initialisers.
            StayFrames = move.TStay;
            TravelFrames = move.TMove;

            if (move.StartsMoving)
            {
                MoveSt = 4;
            }
        }

        /// <summary>
        /// AS3 <c>Interact.command</c>'s <c>isMove</c> arm (<c>Interact.as:1774-1795</c>). Returns
        /// false when the verb is not one of the six platform verbs, so the caller can keep looking.
        ///
        /// <para><b>Gated on <c>isMove</c>, exactly as the oracle gates it.</b> <c>command("move3")</c>
        /// on an object with no <c>move</c> node does nothing at all — it does not even reach the
        /// <c>red</c>/<c>green</c> checks below it, because those are separate <c>if</c>s in a chain that
        /// this one does not short-circuit. Only a real platform answers these.</para>
        /// </summary>
        public bool TryCommand(string verb)
        {
            if (!IsMove)
            {
                return false;
            }

            switch (verb)
            {
                case "stop":
                    MoveSt = 0;
                    return true;

                case "move":
                    MoveSt = 4;
                    return true;

                case "pop":
                    // Interact.as:1784-1793 — a toggle between "idle" and "run forever", and nothing
                    // else. A platform mid-one-shot is already non-zero, so `pop` stops it.
                    MoveSt = MoveSt == 0 ? 4 : 0;
                    return true;

                case "move1":
                    MoveTo(1);
                    return true;

                case "move2":
                    MoveTo(2);
                    return true;

                case "move3":
                    MoveTo(3);
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// AS3 <c>Interact.moveTo</c> (<c>Interact.as:1890-1922</c>) — jump the cycle to the phase
        /// boundary that starts the requested leg, or give up if the platform is already past it.
        ///
        /// <para><b>The two tests are sequential, and the second reads the value the first may have just
        /// written.</b> For <c>n == 1</c> the first band assigns <c>t_move = tStay</c> and the second
        /// band then tests <i>that</i> value. It cannot match unless <c>tMove &lt;= 0</c>, so the order is
        /// currently unobservable — but it is reproduced literally rather than reordered, because a
        /// reordering that happens to agree today is exactly the kind of edit that stops agreeing the
        /// first time a degenerate <c>tmove</c> is authored.</para>
        /// </summary>
        public void MoveTo(int n)
        {
            MoveSt = n;

            if (n == 1)
            {
                if (ElapsedFrames >= 0f && ElapsedFrames < StayFrames)
                {
                    ElapsedFrames = StayFrames;
                }

                if (ElapsedFrames >= StayFrames + TravelFrames &&
                    ElapsedFrames < StayFrames + TravelFrames + StayFrames)
                {
                    MoveSt = 0;
                }
            }

            if (n == 2)
            {
                if (ElapsedFrames >= 0f && ElapsedFrames < StayFrames)
                {
                    MoveSt = 0;
                }

                if (ElapsedFrames >= StayFrames + TravelFrames &&
                    ElapsedFrames < StayFrames + TravelFrames + StayFrames)
                {
                    ElapsedFrames = StayFrames + TravelFrames + StayFrames;
                }
            }

            if (n == 3)
            {
                if (ElapsedFrames >= 0f && ElapsedFrames < StayFrames)
                {
                    ElapsedFrames = StayFrames;
                }

                if (ElapsedFrames >= StayFrames + TravelFrames &&
                    ElapsedFrames < StayFrames + TravelFrames + StayFrames)
                {
                    ElapsedFrames = StayFrames + TravelFrames + StayFrames;
                }
            }
        }

        /// <summary>
        /// AS3 <c>Interact.move</c> (<c>Interact.as:1823-1889</c>). Returns whether the object's
        /// position was written this step.
        ///
        /// <para><b>The phase bands, in order.</b> With <c>S = tStay</c> and <c>T = tMove</c> the cycle
        /// is <c>[0,S)</c> parked at the start, <c>[S,S+T)</c> travelling out, <c>[S+T,2S+T)</c> parked
        /// at the end, <c>[2S+T,2(S+T))</c> travelling back. The <c>moveSt</c> auto-clears are the whole
        /// reason three one-shot verbs exist: <c>2</c> and <c>3</c> give up if the platform has not
        /// started moving yet, and <c>1</c> and <c>3</c> give up once it has arrived. A <c>move3</c>
        /// therefore means "go to the far end if you are at the near end, or if you are mid-flight
        /// outward — otherwise ignore me", which is what makes a button idempotent.</para>
        ///
        /// <para><b>The first band is not chained to the rest, and that is deliberate.</b> The oracle
        /// writes the parked-at-start test as a bare <c>if</c> and then opens a <i>second</i>
        /// <c>if/else if/else if/else</c> chain. In the first band the chain falls through to its
        /// <c>else</c>, which also yields <c>p = 0</c>, so the two spellings agree — but the bare
        /// <c>if</c> is what lets <c>moveSt</c> be cleared and the chain still run in the same step.
        /// Ported as written.</para>
        ///
        /// <para><b>No division by zero is reachable.</b> The two dividing bands require
        /// <c>T &gt; 0</c> to be non-empty, so a <c>tmove="0"</c> node falls to the "parked at the end"
        /// band, which assigns the literal <c>1</c>. No shipped node authors a zero, and none needs
        /// to.</para>
        /// </summary>
        public bool Step()
        {
            if (!IsMove || MoveSt <= 0)
            {
                return false;
            }

            float p = 0f;
            bool wasMoving = MoveP;

            // Interact.as:1828-1831. `dt_move` starts at 1, so this never fires for the shipped data —
            // it is ported because a future authored ramp would otherwise be invisible.
            if (RampStep < 1f)
            {
                RampStep += 0.1f;
            }

            int stay = StayFrames;
            int travel = TravelFrames;

            if (ElapsedFrames >= 0f && ElapsedFrames < stay)
            {
                MoveP = false;
                p = 0f;

                if (MoveSt == 2 || MoveSt == 3)
                {
                    MoveSt = 0;
                }
            }

            if (ElapsedFrames >= stay && ElapsedFrames < stay + travel)
            {
                MoveP = true;
                p = (ElapsedFrames - stay) / travel;
            }
            else if (ElapsedFrames >= stay + travel && ElapsedFrames < stay + travel + stay)
            {
                MoveP = false;
                p = 1f;

                if (MoveSt == 1 || MoveSt == 3)
                {
                    MoveSt = 0;
                }
            }
            else if (ElapsedFrames >= stay + travel + stay && ElapsedFrames < (stay + travel) * 2f)
            {
                MoveP = true;
                p = ((stay + travel) * 2f - ElapsedFrames) / travel;
            }
            else
            {
                MoveP = false;
                p = 0f;
            }

            // Interact.as:1864-1872 plays the `move`/`stop` cue on the edge. The port has no channel to
            // play it on yet, so the edge is surfaced instead and the caller decides.
            bool startedMoving = MoveP != wasMoving;

            float x = BegX + (EndX - BegX) * p;
            float y = BegY + (EndY - BegY) * p;

            BindMove(x, y);

            ElapsedFrames += RampStep;
            if (ElapsedFrames >= CycleFrames)
            {
                ElapsedFrames = 0f;
            }

            if (startedMoving)
            {
                MovingPhaseChanged?.Invoke(this, MoveP);
            }

            return true;
        }

        /// <summary>
        /// AS3 <c>Obj.bindMove</c> (<c>Obj.as:259-266</c>) — write the new anchor and let everything
        /// derived follow.
        ///
        /// <para><b>Why nothing else is updated here.</b> The oracle also rewrites <c>X1/X2/Y1/Y2</c>,
        /// the object's cached bounding box. The port has no such cache: every consumer asks
        /// <c>GetApproximateBounds()</c>, which recomputes from <see cref="ObjectInstance.position"/>
        /// on each call (<c>RoomInstance.cs:1371-1378</c>). So assigning the position <i>is</i> the
        /// whole of <c>bindMove</c> here, and a caller that cached a rect has to re-read it — which is
        /// what <see cref="MovingPlatformSystem.Moved"/> is for.</para>
        ///
        /// <para><b>The displacement is not optional, and it is not written anywhere else.</b> AS3
        /// records it at the end of <c>Box.run</c> — <c>cdx = X - stX</c> (<c>Box.as:643-644</c>), with
        /// <c>stX</c> taken before <c>inter.step()</c> — and a rider reads it on its next frame
        /// (<c>Unit.as:2014-2026</c>: <c>osndx/osndy</c>, gated at ±10 px). The port stores the same
        /// pair in <c>MapObjectDynamicStateData.cdx/cdy</c>, but the only writer is
        /// <c>RoomObjectPhysicsLayer.StepDynamicObject</c>, which skips every non-dynamic prop — and a
        /// platform is <c>DoorOccupancy</c>, never integrated. So without this assignment a platform
        /// moves and its rider does not: the platform slides out from under a unit that is standing
        /// perfectly still, which is indistinguishable from "you cannot stand on it".</para>
        ///
        /// <para><b>Sign convention.</b> <see cref="ObjectInstance.position"/> is the port's y-up
        /// space, so a rising platform has a positive <c>cdy</c>; <c>UnitController</c> and
        /// <c>TilePhysicsController</c> both add it to a y-up step, so no flip is needed here. The
        /// ±10 px gate is in AS3 frames either way, and a platform's per-frame travel in the shipped
        /// data is 3.8 px at most (a 19-tile rise over 200 frames), well inside it.</para>
        /// </summary>
        private void BindMove(float x, float y)
        {
            Vector2 previous = Object.position;
            Object.position = new Vector2(x, y);

            MapObjectDynamicStateData state = Object.runtimeState?.dynamicState;
            if (state == null)
            {
                return;
            }

            state.cdx = x - previous.x;
            state.cdy = y - previous.y;
        }

        /// <summary>Raised on the <c>move</c>/<c>stop</c> edge, with the new <c>moveP</c>.</summary>
        public event Action<MovingPlatformState, bool> MovingPhaseChanged;
    }

    /// <summary>
    /// The per-room driver for <see cref="MovingPlatformState"/> — the port of the
    /// <c>if(this.isMove) this.move()</c> line in AS3's <c>Interact.step</c>
    /// (<c>serv/Interact.as:620-624</c>).
    ///
    /// <para><b>When it runs — measured, not as intended.</b> AS3 reaches that line from
    /// <c>Location.step</c>, which walks the room's object list <i>after</i> <c>gg.step()</c> — the
    /// player — and <i>before</i> the land's scripts (<c>Land.as:1566-1595</c>), i.e. once per rendered
    /// frame at 30 fps. The live driver here is <c>RoomVisualController.LateUpdate</c>, which calls
    /// <see cref="Step"/> once per rendered frame so the frame that advances a platform is the frame
    /// that draws it at its new position. <b>There is no <c>SimTickOrder.MapObjects</c> slot</b> — this
    /// comment used to claim one at order 32, and <c>grep -rn "MapObjects" Assets/_PFE --include=*.cs</c>
    /// finds only the <c>Resources/MapObjects</c> asset paths. The consequence is a real, separate
    /// defect: at 60 fps <see cref="MovingPlatformState.ElapsedFrames"/> advances twice per sim tick, so
    /// a 200-frame travel takes 200 <i>rendered</i> frames rather than 200 sim ticks, and a rider's
    /// carry is one rendered frame's displacement rather than one tick's. Fixing the cadence means
    /// registering a per-room tickable and removing the LateUpdate call — an owner decision, recorded
    /// in <c>docs/OnMapObjects/05_RemainingImplementationWork.md</c> rather than taken here.</para>
    ///
    /// <para><b>What a moving platform does now.</b> It writes <see cref="ObjectInstance.position"/>
    /// plus its own <c>cdx</c>/<c>cdy</c>, and it is registered as a <b>support surface</b> by
    /// <c>RoomObjectPhysicsLayer</c> (see its <c>_staticShelfObjects</c> block): <c>platform2</c> and
    /// <c>platform3</c> author <c>shelf="1"</c>, so a unit can stand on them and is carried by their
    /// displacement. What it still does not do is block <i>horizontal</i> movement, which is also true
    /// in the oracle — a prop is a one-way floor, not a wall; only <c>door=</c> objects are stamped into
    /// the tile grid (<c>AreaTriggerSystem.UpdateDoorTileCollision</c>).</para>
    /// </summary>
    public sealed class MovingPlatformSystem
    {
        private readonly List<MovingPlatformState> _platforms = new List<MovingPlatformState>();
        private readonly Dictionary<ObjectInstance, MovingPlatformState> _byObject =
            new Dictionary<ObjectInstance, MovingPlatformState>();

        /// <summary>The room this system drives, or null for a system built with no room.</summary>
        public RoomInstance Room { get; }

        /// <summary>How many objects in this room author a <c>move</c> node.</summary>
        public int Count => _platforms.Count;

        /// <summary>The states, for a fixture or an overlay.</summary>
        public IReadOnlyList<MovingPlatformState> Platforms => _platforms;

        /// <summary>Raised once per platform per step in which its position was written.</summary>
        public event Action<MovingPlatformState> Moved;

        public MovingPlatformSystem(RoomInstance room)
        {
            Room = room;
            Rebuild();
        }

        /// <summary>
        /// Rebuild from the room's current object list. Called by the constructor and by a caller that
        /// has changed the list — the same lifecycle as <c>RoomObjectPhysicsLayer.Rebuild</c>, because
        /// the two are driven by the same event (a room being populated or re-populated).
        /// </summary>
        public void Rebuild()
        {
            _platforms.Clear();
            _byObject.Clear();

            if (Room?.objects == null)
            {
                return;
            }

            for (int i = 0; i < Room.objects.Count; i++)
            {
                ObjectInstance obj = Room.objects[i];
                if (obj == null || !obj.TryGetMoveNode(out MapObjectMoveData move))
                {
                    continue;
                }

                var state = new MovingPlatformState(obj, in move);
                _platforms.Add(state);
                _byObject[obj] = state;
            }
        }

        /// <summary>
        /// Advance every running platform by one frame. Returns how many wrote a position.
        /// </summary>
        public int Step()
        {
            int moved = 0;

            for (int i = 0; i < _platforms.Count; i++)
            {
                MovingPlatformState state = _platforms[i];
                if (state.Step())
                {
                    moved++;
                    Moved?.Invoke(state);
                }
            }

            return moved;
        }

        /// <summary>
        /// Route one of the six platform verbs to an object, if it is a platform.
        ///
        /// <para>This is what the <c>allid</c> broadcast lands on: a button carrying
        /// <c>allact="move3" allid="1"</c> sends <c>move3</c> to every object tagged <c>allid="1"</c>,
        /// and the platforms among them are the ones that answer.</para>
        /// </summary>
        public bool TryCommand(ObjectInstance obj, string verb)
        {
            if (obj == null || !_byObject.TryGetValue(obj, out MovingPlatformState state))
            {
                return false;
            }

            return state.TryCommand(verb);
        }

        /// <summary>Whether this object is a platform.</summary>
        public bool IsPlatform(ObjectInstance obj)
        {
            return obj != null && _byObject.ContainsKey(obj);
        }

        /// <summary>The state for an object, or null when it is not a platform.</summary>
        public MovingPlatformState StateFor(ObjectInstance obj)
        {
            return obj != null && _byObject.TryGetValue(obj, out MovingPlatformState state) ? state : null;
        }
    }
}
