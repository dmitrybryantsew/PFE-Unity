using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Map.Generation
{
    /// <summary>
    /// AS3 <c>Land.buildProb(nprob)</c> (<c>Land.as:771-807</c>): construct the detached room a prob door
    /// opens into.
    ///
    /// <para><b>What the oracle does, and which parts are here.</b></para>
    /// <list type="number">
    /// <item>Find the room in the <c>prob</c> land's collection whose name is the prob id
    /// (<c>:783-787</c>). <see cref="FindRoom"/> does this.</item>
    /// <item>Build a <c>Location</c> from it at <c>(0,0,0)</c>, tag it <c>landProb = nprob</c> and
    /// <c>noMap = true</c>, and attach a <c>Probation</c> if the land declares one (<c>:789-796</c>).
    /// <see cref="Build"/> does the room, the coordinate and the tag. <b>The <c>Probation</c> is not
    /// built here</b> — see the note on <see cref="Build"/>.</item>
    /// <item>Drop a <c>doorout</c> box on the room's first spawn point (<c>:797-800</c>).
    /// <see cref="RoomPopulator.PlaceReturnDoor"/> does this.</item>
    /// <item>Populate it — <c>setObjects()</c>. AS3 runs this in <c>buildProbs</c>, one pass later than
    /// <c>buildProb</c> itself (<c>:760-766</c>), on every location it collected. <see cref="Build"/>
    /// does it inline instead, so a room this method returns is complete rather than a shell waiting on
    /// a caller. See the note on the call for why skipping it made the exit room's own door
    /// unreachable.</item>
    /// <item>Cache it under <c>probs[nprob]</c> and append it to <c>listLocs</c> (<c>:801-802</c>).
    /// That is the caller's job — this type builds one room and does not own a cache, because a cache
    /// is run state and this is a constructor.</item>
    /// </list>
    ///
    /// <para><b>Why the room is detached by its grid, not by a bogus coordinate.</b> AS3 detaches it
    /// with <c>noMap</c>: the room lives in <c>probs[id]</c> and never enters <c>locs[x][y][z]</c>. The
    /// port detaches it the same way — it is registered in <c>ProbDoorContext</c> and never in
    /// <c>LandMap.rooms</c>, and a grid step out of it is refused
    /// (<see cref="ProbTransition.AdmitsGridStep"/>). It is <i>not</i> detached by building it at a
    /// coordinate no cell can hold: a room's coordinate is also its world position, so the sentinel made
    /// the prob room render and collide ~4e10 units from the player it had just moved into it. See
    /// <see cref="ProbTransition.ProbRoomLandPosition"/>.</para>
    /// </summary>
    public static class ProbRoomBuilder
    {
        /// <summary>
        /// The prob land's room whose AS3 name is <paramref name="probId"/>
        /// (<c>Land.as:786</c>, <c>xml.@name == nprob</c>).
        ///
        /// <para>Matched against <see cref="RoomTemplate.id"/>, the bare AS3 room name, and not against
        /// <c>GetContentId()</c> — the latter is <c>"&lt;collection&gt;/&lt;id&gt;"</c>, so it would never
        /// equal a prob id.</para>
        /// </summary>
        /// <returns>The template, or <c>null</c> when the collection has no such room.</returns>
        public static RoomTemplate FindRoom(IReadOnlyList<RoomTemplate> probLandRooms, string probId)
        {
            if (probLandRooms == null || string.IsNullOrEmpty(probId)) return null;

            for (int i = 0; i < probLandRooms.Count; i++)
            {
                RoomTemplate template = probLandRooms[i];
                if (template != null && string.Equals(template.id, probId, StringComparison.Ordinal))
                {
                    return template;
                }
            }

            return null;
        }

        /// <summary>
        /// Build the detached prob room for <paramref name="template"/>.
        /// </summary>
        /// <param name="generator">The room factory. Null is refused rather than defaulted.</param>
        /// <param name="template">The prob room's template, from <see cref="FindRoom"/>.</param>
        /// <param name="probId">AS3 <c>nprob</c> — stored on the room as its identity.</param>
        /// <returns>
        /// The room, or <c>null</c> when <paramref name="generator"/> or <paramref name="template"/> is
        /// null. AS3 returns <c>false</c> in the equivalent case (<c>Land.as:779-782</c>, already built;
        /// and the loop simply finds no room if the name is wrong).
        /// </returns>
        /// <remarks>
        /// <b>The <c>Probation</c> is deliberately absent.</b> AS3 attaches one here
        /// (<c>Land.as:795</c>), and it is the whole runtime of a prob room: the <c>&lt;con&gt;</c> checks,
        /// the wave timer, the seal-until-cleared behaviour and the completion counter.
        /// <see cref="ProbationState"/> is now the port of those <i>rules</i> — but it is a pure state
        /// machine that has to be driven, and the driver is a MonoBehaviour: it must apply
        /// <see cref="ProbEffects"/> to real props, call <c>step</c> from the room's tick, and call
        /// <c>check</c> from the interaction paths. Attaching a state machine with no driver would make
        /// every prob room <i>look</i> finished while closing nothing. The room, its return door and its
        /// identity are what make the door's target real; the driver is a separate workstream.
        /// </remarks>
        public static RoomInstance Build(RoomGenerator generator, RoomTemplate template, string probId)
        {
            if (generator == null) throw new ArgumentNullException(nameof(generator));
            if (template == null) return null;

            RoomInstance room = generator.GenerateRoom(template, ProbTransition.ProbRoomLandPosition);
            if (room == null) return null;

            room.probId = probId ?? string.Empty;

            // AS3 guards on `loc.spawnPoints.length`; PlaceReturnDoor repeats the guard so a spawnless
            // prob room is built without a door rather than with one at an invented position.
            RoomPopulator.PlaceReturnDoor(room);

            // setObjects() — the step this used to skip, and the one that made every prob room an empty
            // shell. `buildProb` only *constructs* the Location; the objects come from `buildProbs`,
            // which walks every location it appended — prob rooms included, because buildProb pushes them
            // into `listLocs` (Land.as:802) — and calls setObjects/preStep/prepare on each
            // (Land.as:760-766). Without this, `exit_plant` renders its tiles and its return door and
            // nothing else: no `work`, no `checkpoint`, and no `exit` box, so the room's own level
            // advance is unreachable and the land can never descend.
            //
            // Order is the oracle's, not a preference: the `doorout` is created inside buildProb
            // (:797-800), so it precedes the template's objects in `objs`.
            RoomPopulator.PopulateRoom(room, template, room.difficulty);

            return room;
        }
    }
}
