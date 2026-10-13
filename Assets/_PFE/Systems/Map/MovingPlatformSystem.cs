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

            // AS3 reads X/Y off the object, which at this point are the placement's authored
            // coordinates (Interact.as:429-431). The port stores the same pair in `position`.
            BegX = obj.position.x;
            BegY = obj.position.y;

            // Interact.as:432-451. The `@dx`-absent arm writes `endX = endX2 = X`; the port has no
            // `endX2` (it is only ever assigned, never read in the oracle), so only endX is kept.
            EndX = move.HasDx
                ? BegX + (move.Mirror ? -move.Dx : move.Dx) * WorldConstants.TILE_SIZE
                : BegX;

            EndY = move.HasDy
                ? BegY + move.Dy * WorldConstants.TILE_SIZE
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
        /// </summary>
        private void BindMove(float x, float y)
        {
            Object.position = new Vector2(x, y);
        }

        /// <summary>Raised on the <c>move</c>/<c>stop</c> edge, with the new <c>moveP</c>.</summary>
        public event Action<MovingPlatformState, bool> MovingPhaseChanged;
    }

    /// <summary>
    /// The per-room driver for <see cref="MovingPlatformState"/> — the port of the
    /// <c>if(this.isMove) this.move()</c> line in AS3's <c>Interact.step</c>
    /// (<c>serv/Interact.as:620-624</c>).
    ///
    /// <para><b>When it runs.</b> AS3 reaches that line from <c>Location.step</c>, which walks the
    /// room's object list <i>after</i> <c>gg.step()</c> — the player — and <i>before</i> the land's
    /// scripts (<c>Land.as:1566-1595</c>). The port mirrors that by ticking at
    /// <c>SimTickOrder.MapObjects</c> (32): after <c>PlayerMotor</c> (20) and <c>UnitsAndAi</c> (30),
    /// before <c>Scripts</c> (75). It is after the motors on purpose — a platform that wrote its
    /// position before the unit standing on it had moved would have that write overwritten in the same
    /// tick.</para>
    ///
    /// <para><b>What a moving platform still does not do in this port.</b> It writes
    /// <see cref="ObjectInstance.position"/> and nothing else. Solid collision for these objects is
    /// tile-stamped in the port (<c>AreaTriggerSystem.UpdateDoorTileCollision</c>) and only for
    /// definitions that author <c>door=</c>; the objects that actually move — <c>platform2</c>,
    /// <c>platform3</c>, <c>wturret</c>, <c>knop2</c> — author none, and <c>blocksMovement</c> is
    /// imported from <c>phis</c> but read by nothing. So a platform moves and is drawn moving, and the
    /// player still walks through it. That is a separate workstream and is reported, not papered
    /// over.</para>
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
