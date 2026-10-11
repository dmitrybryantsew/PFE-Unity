using UnityEngine;

namespace PFE.Systems.Map.Generation
{
    /// <summary>
    /// AS3 <c>Land.gotoProb(param1, param2, param3)</c> (<c>Land.as:1391-1438</c>) as a pure decision:
    /// entering a prob room saves where the player came from, and the return door puts them back.
    ///
    /// <para><b>Why the save/restore is split out from the transition.</b> The oracle's function does two
    /// things: it decides the new <i>coordinates</i> (pure, and where all the traps are), and it calls
    /// <c>ativateLoc()</c> to actually swap rooms (engine work). Only the first is decidable here, and it
    /// carries the rules worth pinning. The activation itself is
    /// <c>RoomTransitionManager.PerformTransition</c>'s job and needs a play test.</para>
    ///
    /// <para><b>The three rules, in the order the oracle applies them.</b></para>
    /// <list type="number">
    /// <item><b>The saved position is the door's, not the player's, when the caller supplies one.</b>
    /// <c>param2 &lt; 0 || param3 &lt; 0</c> falls back to <c>gg.X/gg.Y</c> (<c>:1417-1424</c>), and the
    /// caller <i>always</i> supplies the door's own position (<c>Interact.as:1565</c>,
    /// <c>:1576</c> pass <c>this.owner.X, this.owner.Y</c>) — so the fallback is for a scripted entry, not
    /// for the door path. Passing the player's position here would put the player back inside the door
    /// they just walked through rather than on the tile in front of it.</item>
    /// <item><b>A saved position of exactly <c>(0, 0)</c> means "none recorded", not "the corner".</b>
    /// The return branch tests <c>retX == 0 &amp;&amp; retY == 0</c> and uses the room's spawn point
    /// instead (<c>:1401-1408</c>). This is the "a dummy value that is also a legal value" shape
    /// (lesson #118): <c>(0,0)</c> is room-local, i.e. the top-left tile, which is a wall in every room —
    /// so in practice the sentinel is unreachable as a real position, and the rule is safe. It is
    /// reproduced rather than replaced by a nullable because the oracle's behaviour at <c>(0,0)</c> is
    /// observable and this must match it.</item>
    /// <item><b>A failed entry rolls the coordinates back.</b> <c>if(ativateLoc()) … else { prob = "";
    /// locX = retLocX; … }</c> (<c>:1427-1436</c>). The rollback restores the room but <i>not</i> the
    /// player — the player was never moved, because the room was never activated. So the caller must not
    /// teleport on the failure path.</item>
    /// </list>
    /// </summary>
    public static class ProbTransition
    {
        /// <summary>The <c>allact</c> the return door carries (<c>AllData.as:5017</c>).</summary>
        public const string ReturnActionId = "probreturn";

        /// <summary>
        /// The land coordinate of a prob room: <b>the origin</b>. AS3 writes <c>locX = locY = locZ = 0</c>
        /// on entry (<c>:1425</c>) and builds the room at <c>newLoc(room, 0, 0, 0, …)</c>
        /// (<c>Land.as:789</c>) — one coordinate, used for both.
        ///
        /// <para><b>It is a real coordinate and the room really is there.</b> Every world quantity the
        /// port derives from a room — its render origin, its <c>ITileQueryService.OriginPixel</c> and
        /// therefore its collision geometry, and its player spawn — is computed from
        /// <c>RoomInstance.landPosition</c>. A prob room built at an out-of-grid sentinel therefore
        /// rendered, collided and spawned ~4e10 units away from the player the entry had just moved,
        /// which is why entering one produced a blank screen. The same origin also collapsed every
        /// chain vertex to a single value: at a ~4e12-pixel origin a <c>float</c> cannot represent the
        /// sub-unit tile offset added in <c>RoomChainGeometry.EmitChain</c>, so all six points landed on
        /// one coordinate and Box2D rejected the degenerate chain.</para>
        ///
        /// <para><b>What is genuinely different about a prob room is its grid, not its position.</b>
        /// AS3 keeps it in <c>probs[id]</c> with <c>noMap = true</c> (<c>:791</c>) rather than in
        /// <c>locs[x][y][z]</c>, and <c>gotoLoc</c> branches on <c>this.prob</c> to read
        /// <c>this.probs[this.prob]</c> instead of <c>this.locs</c> (<c>:1335</c> vs <c>:1343</c>). A
        /// prob land's grid holds exactly one room — this one, at this coordinate — so every neighbour
        /// lookup misses and the step refuses. That is <see cref="AdmitsGridStep"/>, and it is what keeps
        /// a prob room off the land grid; a bogus coordinate never did.</para>
        /// </summary>
        public static readonly Vector3Int ProbRoomLandPosition = Vector3Int.zero;

        /// <summary>
        /// Whether a grid step (AS3 <c>Land.gotoLoc</c>, <c>Land.as:1302</c>) may start from
        /// <paramref name="room"/>.
        ///
        /// <para><b>The oracle's own test, at the oracle's own place.</b> <c>gotoLoc</c> chooses which
        /// grid it reads a neighbour from by asking whether the player is in a prob
        /// (<c>this.prob == ""</c> → <c>locs</c>, otherwise <c>probs[this.prob]</c>,
        /// <c>:1335-1350</c>). The prob land's grid holds one room, so a step off its edge finds
        /// <c>null</c> and refuses (<c>:1343-1349</c>; direction 3 returns <c>{"die":true}</c> instead,
        /// which is the pit branch, not a room step). <c>RoomInstance.probId</c> is non-empty for exactly
        /// the rooms AS3 keeps in <c>probs</c>, so it is this port's <c>this.prob</c>.</para>
        ///
        /// <para><b>Why this is load-bearing rather than belt-and-braces.</b> The port computes an edge
        /// step from <c>current.landPosition</c>
        /// (<c>RoomTransitionManager.TransitionThroughEdge</c>), so with a prob room at the origin a
        /// player walking to its edge would step into the <i>real</i> land's origin cell or one of its
        /// neighbours. AS3 cannot do that, because its lookup goes to the prob land's grid — which is
        /// the entire reason this predicate exists.</para>
        /// </summary>
        public static bool AdmitsGridStep(RoomInstance room)
        {
            return room != null && string.IsNullOrEmpty(room.probId);
        }

        /// <summary>
        /// The entry half: what to remember so the return door can put the player back.
        /// </summary>
        /// <param name="currentLandPosition">AS3 <c>locX/locY/locZ</c> — the room being left.</param>
        /// <param name="playerX">AS3 <c>gg.X</c>.</param>
        /// <param name="playerY">AS3 <c>gg.Y</c>.</param>
        /// <param name="doorX">
        /// AS3 <c>param2</c> — the door's own X. A negative value means "not supplied", which is the
        /// oracle's own sentinel (<c>param2 &lt; 0 || param3 &lt; 0</c>, <c>:1417</c>).
        /// </param>
        /// <param name="doorY">AS3 <c>param3</c>.</param>
        public static ProbReturnPoint SaveReturnPoint(
            Vector3Int currentLandPosition,
            float playerX,
            float playerY,
            float doorX = -1f,
            float doorY = -1f)
        {
            bool hasDoorPosition = doorX >= 0f && doorY >= 0f;

            return new ProbReturnPoint(
                currentLandPosition,
                hasDoorPosition ? doorX : playerX,
                hasDoorPosition ? doorY : playerY);
        }

        /// <summary>
        /// The return half: where the player lands when the return door is used.
        ///
        /// <para>Returns the saved room, and either the saved position or — when that position is the
        /// <c>(0,0)</c> sentinel — a request for the room's own spawn point (<c>:1401-1408</c>). The
        /// spawn point itself is not resolved here: it needs the room, which is the adapter's to
        /// supply.</para>
        /// </summary>
        public static ProbArrival PlanReturn(ProbReturnPoint saved)
        {
            bool hasSavedPosition = !(saved.PlayerX == 0f && saved.PlayerY == 0f);

            return new ProbArrival(
                saved.LandPosition,
                hasSavedPosition ? ProbArrivalKind.SavedPlayerPosition : ProbArrivalKind.RoomSpawnPoint,
                saved.PlayerX,
                saved.PlayerY);
        }

        /// <summary>
        /// The room to restore when the entry failed to activate (<c>:1431-1435</c>). The player is not
        /// moved: the failed entry never moved them.
        /// </summary>
        public static Vector3Int RollbackLandPosition(ProbReturnPoint saved)
        {
            return saved.LandPosition;
        }

        /// <summary>
        /// Whether a return point records a usable position — the oracle's
        /// <c>!(retX == 0 &amp;&amp; retY == 0)</c>, exposed so a diagnostic can report the sentinel case
        /// rather than silently substituting the spawn point.
        /// </summary>
        public static bool HasRecordedPosition(ProbReturnPoint saved)
        {
            return !(saved.PlayerX == 0f && saved.PlayerY == 0f);
        }
    }

    /// <summary>Where the player came from, so a prob room's return door can undo the entry.</summary>
    public readonly struct ProbReturnPoint
    {
        /// <summary>AS3 <c>retLocX/retLocY/retLocZ</c> — the room that was left.</summary>
        public readonly Vector3Int LandPosition;

        /// <summary>AS3 <c>retX</c>. Meaningless when the pair is <c>(0,0)</c> — see rule 2.</summary>
        public readonly float PlayerX;

        /// <summary>AS3 <c>retY</c>.</summary>
        public readonly float PlayerY;

        /// <summary>Test-friendly construction; the public fields are readonly by design.</summary>
        public ProbReturnPoint(Vector3Int landPosition, float playerX, float playerY)
        {
            LandPosition = landPosition;
            PlayerX = playerX;
            PlayerY = playerY;
        }
    }

    /// <summary>How the return door decides where to put the player.</summary>
    public enum ProbArrivalKind
    {
        /// <summary>Use <see cref="ProbArrival.PlayerX"/>/<c>PlayerY</c>.</summary>
        SavedPlayerPosition,

        /// <summary>The saved position was the <c>(0,0)</c> sentinel: ask the room for its spawn point.</summary>
        RoomSpawnPoint,
    }

    /// <summary>The return door's arrival, as decided by <see cref="ProbTransition.PlanReturn"/>.</summary>
    public readonly struct ProbArrival
    {
        /// <summary>The room to re-activate.</summary>
        public readonly Vector3Int LandPosition;

        /// <summary>Which of the two position sources to use.</summary>
        public readonly ProbArrivalKind Kind;

        /// <summary>AS3 <c>retX</c>, valid only for <see cref="ProbArrivalKind.SavedPlayerPosition"/>.</summary>
        public readonly float PlayerX;

        /// <summary>AS3 <c>retY</c>.</summary>
        public readonly float PlayerY;

        public ProbArrival(Vector3Int landPosition, ProbArrivalKind kind, float playerX, float playerY)
        {
            LandPosition = landPosition;
            Kind = kind;
            PlayerX = playerX;
            PlayerY = playerY;
        }
    }
}
