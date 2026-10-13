using System;
using System.Collections.Generic;

namespace PFE.Systems.Map.Actions
{
    /// <summary>
    /// The port of AS3 <c>Location.allAct(obj, verb, allId)</c> (<c>loc/Location.as:2835-2860</c>) — the
    /// <b>tagged broadcast</b> that every platform button in the game runs on.
    ///
    /// <para><b>This is the oracle's default branch, not a special case.</b> <c>Interact.allAct</c> is a
    /// long <c>if/else if</c> chain of about twenty explicit <c>allact</c> values
    /// (<c>prob</c>, <c>probreturn</c>, <c>hack_robot</c>, <c>hack_lock</c>, <c>prob_help</c>,
    /// <c>electro_check</c>, <c>comein</c>, <c>bind</c>, <c>work</c>/<c>lab</c>/<c>stove</c>,
    /// <c>app</c>, <c>map</c>, <c>stand</c>, <c>exit</c>, <c>robocell</c>, <c>alarm</c>,
    /// <c>vault</c>) whose <c>else</c> is:</para>
    /// <code>
    /// else { this.owner.loc.allAct(this.owner, this.allact, this.allid); }
    /// </code>
    /// <para>So <b>every</b> <c>allact</c> value that is not in that list is a broadcast. And the
    /// measured data says that is most of them: of the 214 placement-level <c>allact</c> attributes in
    /// the shipped rooms, <c>open</c> (94), <c>move3</c> (36), <c>swap</c> (32), <c>spawn</c> (17),
    /// <c>unlock</c> (10), <c>onoff</c> (9), <c>close</c> (5), <c>move</c> (2), <c>pop</c> (2),
    /// <c>hack</c> (2) and <c>activate</c> (1) — 210 of them — take this path. Only the four
    /// <c>comein</c> doors do not.</para>
    ///
    /// <para><b>And the sender is never the target.</b> Every one of those 214 is carried by a
    /// <i>button</i> — <c>knop1</c>×51, <c>knop2</c>×11, <c>knop3</c>×11, <c>knop4</c>×2, <c>term</c>×3,
    /// <c>term2</c>×3, <c>term3</c>×1, <c>area</c>×4, <c>trlaser</c>×7, <c>door3</c>×1,
    /// <c>wallsafe</c>×1, <c>doorboss</c>×4 — and never by the door or platform it acts on. The
    /// <c>_loc4_ != param1</c> test is what keeps a button from acting on itself, and it is load-bearing
    /// in both directions: without it, a <c>knop1</c> carrying <c>allid="1"</c> would open itself along
    /// with the doors.</para>
    ///
    /// <para><b>Three loops, three different rules, and the differences are real.</b> The oracle walks
    /// <c>objs</c>, then <c>areas</c>, then <c>units</c>. Objects and units get the
    /// <c>inter &amp;&amp; (allId == "" || allId == null || inter.allid == allId)</c> test and are called
    /// as <c>command(verb, "13")</c>; areas get a <i>different</i> test and are called as
    /// <c>command(verb)</c> with no second argument. See <see cref="Run"/> for both.</para>
    /// </summary>
    public sealed class ObjectInteractionBroadcast
    {
        private readonly ObjectVerbCommand _verbs;

        private readonly Action<string> _report;

        private readonly HashSet<string> _reported =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Sink for the unit half of the broadcast — AS3's <c>Unit.command</c>
        /// (<c>unit/Unit.as:4784-4810</c>: <c>activate</c>, <c>fraction</c>) and
        /// <c>UnitTurret.command</c> (<c>unit/UnitTurret.as:465-489</c>: <c>shoot</c>, <c>alarma</c>,
        /// <c>hack</c>, <c>port</c>). Null is tolerated and reported: a room whose units cannot be
        /// addressed still moves its platforms and opens its doors.
        /// </summary>
        public Action<RoomInstance, UnitInstance, string, string> UnitVerbs;

        /// <summary>
        /// AS3 <c>Area.command</c>'s presentation half — <c>setLift()</c> and the
        /// <c>vis.alpha = enabled ? 1 : 0.1</c> line (<c>Area.as:269-283</c>). Null is fine: an area's
        /// opacity is cosmetic and a room with no visual manager still reacts.
        /// </summary>
        public Action<RoomInstance, ObjectInstance> AreaPresentation;

        /// <summary>
        /// AS3 <c>Area.command("dam", n)</c> → <c>damTiles(int(param2))</c> — the area that destroys the
        /// tiles it covers. Null is fine and is the shipped state: no area in the data broadcasts
        /// <c>dam</c>.
        /// </summary>
        public Action<RoomInstance, ObjectInstance, int> AreaDamageTiles;

        public ObjectInteractionBroadcast(
            ObjectVerbCommand verbs,
            Action<RoomInstance, UnitInstance, string, string> unitVerbs = null,
            Action<string> report = null)
        {
            _verbs = verbs ?? throw new ArgumentNullException(nameof(verbs));
            UnitVerbs = unitVerbs;
            _report = report ?? (_ => { });
        }

        /// <summary>
        /// Send <paramref name="verb"/> to everything in <paramref name="room"/> tagged
        /// <paramref name="allId"/>, skipping <paramref name="sender"/>.
        ///
        /// <para><b>The area rule is not a typo, and reproducing it matters.</b> The oracle writes the
        /// area guard as</para>
        /// <code>
        /// if(_loc4_ != param1 &amp;&amp; param3 == "" || param3 == null || (_loc4_ as Area).allid == param3)
        /// </code>
        /// <para>and in ActionScript <c>&amp;&amp;</c> binds tighter than <c>||</c>, so this parses as
        /// <c>(area != sender &amp;&amp; tag == "") || tag == null || area.allid == tag</c>. With a
        /// non-empty tag — which is every placement-level broadcast in the shipped data — it collapses
        /// to <c>area.allid == tag</c>, the same as the object rule. With an <b>empty</b> tag it is
        /// <c>true</c> for every area <i>including the sender</i>, where the object rule would still have
        /// excluded it. The port reproduces both arms rather than "fixing" the precedence, because the
        /// empty-tag case is reachable: a definition-level <c>allact</c> has no <c>allid</c> to carry
        /// (<c>p_luna</c>'s <c>take</c> is the only one), and an area reached that way is meant to be
        /// swept up.</para>
        ///
        /// <para><b>The missing <c>"13"</c> on the area call is also the oracle's.</b> Areas are called
        /// as <c>command(param2)</c> while objects and units get <c>command(param2, "13")</c>. The
        /// consequence is that an area whose own <c>allact</c> is <c>open</c> re-enters its own
        /// <c>allAct</c> from inside <c>Interact.command</c>, while an object does not. It is a
        /// one-character difference with a visible effect, so it is preserved and commented rather than
        /// normalised.</para>
        /// </summary>
        /// <returns>How many targets accepted the verb.</returns>
        public int Run(RoomInstance room, ObjectInstance sender, string verb, string allId = "")
        {
            if (room?.objects == null || string.IsNullOrEmpty(verb))
            {
                return 0;
            }

            string tag = allId ?? string.Empty;
            int sent = 0;

            for (int i = 0; i < room.objects.Count; i++)
            {
                ObjectInstance target = room.objects[i];
                if (target == null)
                {
                    continue;
                }

                if (IsArea(target))
                {
                    bool areaMatches = tag.Length == 0 || MatchesTag(target, tag);
                    if (areaMatches &&
                        AreaVerbCommand.Apply(
                            room, target, verb, null, AreaPresentation, AreaDamageTiles))
                    {
                        sent++;
                    }

                    continue;
                }

                if (ReferenceEquals(target, sender))
                {
                    continue;
                }

                if (tag.Length != 0 && !MatchesTag(target, tag))
                {
                    continue;
                }

                if (_verbs.Apply(room, target, verb, ObjectVerbCommand.BroadcastParam))
                {
                    sent++;
                }
            }

            if (room.units != null)
            {
                for (int i = 0; i < room.units.Count; i++)
                {
                    UnitInstance unit = room.units[i];
                    if (unit == null)
                    {
                        continue;
                    }

                    // `_loc4_ != param1` compares a Unit against an Obj. The two lists are disjoint in
                    // the oracle, so the test never fires; here a unit is never the sender either, since
                    // a sender is always an object. Kept as a comment rather than a dead comparison.
                    if (tag.Length != 0 && !MatchesTag(unit, tag))
                    {
                        continue;
                    }

                    sent += SendToUnit(room, unit, verb, null);
                }
            }

            return sent;
        }

        /// <summary>
        /// AS3 <c>Area.step</c>'s own broadcast (<c>loc/Area.as:336-339</c>).
        ///
        /// <para><b>An area is not a button.</b> Where a <c>knop</c> fires its <c>allact</c> once per
        /// press, an active area runs <c>loc.allAct(this, this.allact, this.allid)</c> on <b>every
        /// frame</b> — gated only on <c>active &amp;&amp; !preactive</c>. That is the whole difference
        /// between "press to open" and "stand here to keep it open", and it is why the port needs this
        /// method at all rather than folding areas into the one-shot path.</para>
        /// </summary>
        public int RunFromArea(RoomInstance room, ObjectInstance area)
        {
            if (area == null)
            {
                return 0;
            }

            string verb = area.GetAllAct();
            if (string.IsNullOrEmpty(verb))
            {
                return 0;
            }

            return Run(room, area, verb, area.GetAllId());
        }

        /// <summary>
        /// AS3 <c>Script.as:296-299</c> — <c>World.w.loc.allAct(null, val, n)</c>. The sender is null,
        /// so nothing is excluded.
        /// </summary>
        public int RunFromScript(RoomInstance room, string verb, string allId)
        {
            return Run(room, null, verb, allId);
        }

        private int SendToUnit(RoomInstance room, UnitInstance unit, string verb, string param)
        {
            if (UnitVerbs == null)
            {
                ReportOnce(
                    "unit-verbs",
                    "`" + verb + "` was broadcast to a unit but no unit-verb sink is wired, so '" +
                    (string.IsNullOrEmpty(unit.unitId) ? "?" : unit.unitId) + "' did not react. " +
                    "Unit.command / UnitTurret.command live in the unit layer.");
                return 0;
            }

            UnitVerbs(room, unit, verb, param);
            return 1;
        }

        /// <summary>
        /// AS3 <c>_loc4_.inter.allid == param3</c> — an exact, case-sensitive comparison, because the
        /// authored tags are short mixed strings (<c>1</c>, <c>a1</c>, <c>d2</c>, <c>air1</c>) and AS3's
        /// <c>==</c> on two Strings is ordinal.
        ///
        /// <para>A unit carries its <c>allid</c> on the placement's attribute list the same way an object
        /// does (<c>UnitInstance.attributes</c>), so the two are read identically.</para>
        /// </summary>
        private static bool MatchesTag(ObjectInstance target, string tag)
        {
            return string.Equals(target.GetAllId(), tag, StringComparison.Ordinal);
        }

        private static bool MatchesTag(UnitInstance unit, string tag)
        {
            string value = MapObjectDataUtility.GetAttribute(unit.attributes, "allid", string.Empty);
            return string.Equals(value, tag, StringComparison.Ordinal);
        }

        /// <summary>
        /// Whether this object is an AS3 <c>Area</c> rather than an <c>Obj</c>.
        ///
        /// <para><b>The oracle keeps them in two lists; the port keeps them in one.</b>
        /// <c>Location.setObjects</c> routes an <c>&lt;obj id="area"&gt;</c> node to <c>new Area(...)</c>
        /// and pushes it to <c>this.areas</c> (<c>Location.as:2051-2052</c>), never to <c>this.objs</c> —
        /// so the two loops above never see the same object. The port's importer writes every placement
        /// into <c>RoomInstance.objects</c> with an <c>objectType</c> string, so the split has to be
        /// re-derived from that string. 4 placements in the shipped rooms are areas.
        /// </para>
        /// </summary>
        private static bool IsArea(ObjectInstance obj)
        {
            return string.Equals(obj.objectType, "area", StringComparison.OrdinalIgnoreCase);
        }

        private void ReportOnce(string id, string message)
        {
            if (_reported.Add(id))
            {
                _report("[Broadcast] " + message);
            }
        }
    }
}
