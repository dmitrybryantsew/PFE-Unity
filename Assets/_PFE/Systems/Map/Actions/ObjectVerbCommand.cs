using System;

namespace PFE.Systems.Map.Actions
{
    /// <summary>
    /// Everything <see cref="ObjectVerbCommand"/> may need from the world, all optional.
    ///
    /// <para><b>Why a bag of delegates rather than an interface.</b> Each member is a capability that a
    /// <i>different</i> part of the port owns, and none of them is always present: the tile stamper
    /// lives in <c>AreaTriggerSystem</c>, the visual state in <c>RoomObjectVisualManager</c>, the info
    /// text in the spell host, the platform states in <c>MovingPlatformSystem</c>. An interface would
    /// force every caller to implement all of them to use one, and the established shape in this
    /// codebase for exactly that problem is a null-tolerant delegate set that reports by name
    /// (<c>ObjectActionDispatcher.CreateDefault</c>). A missing delegate is reported once, and the verb
    /// still changes whatever state it owns.</para>
    /// </summary>
    public sealed class ObjectVerbHost
    {
        /// <summary>Sink for "this verb needed a capability that is not wired". Defaults to nothing at all, so a fixture can run silently and a scene can supply its own.</summary>
        public Action<string> Report;

        /// <summary>
        /// AS3 <c>Interact.setAct("open", n)</c>'s <c>setDoor()</c> half (<c>Interact.as:853-856</c>) —
        /// restamp the object's tiles solid or open. In the port that is
        /// <c>AreaTriggerSystem.UpdateDoorTileCollision</c>.
        /// </summary>
        public Action<RoomInstance, ObjectInstance, bool> StampDoorTiles;

        /// <summary>AS3 <c>owner.setVisState(name)</c> — play the <c>open</c>/<c>close</c> clip.</summary>
        public Action<RoomInstance, ObjectInstance, string> SetVisualState;

        /// <summary>AS3 <c>World.w.gui.infoText(key)</c> — the banner a terminal prints.</summary>
        public Action<string> InfoText;

        /// <summary>AS3 <c>Interact.signal(colour)</c> — the <c>red</c>/<c>green</c> emitter burst.</summary>
        public Action<RoomInstance, ObjectInstance, string> Signal;

        /// <summary>AS3 <c>Interact.explosion()</c> — the <c>dam</c> verb's explosive arm.</summary>
        public Action<RoomInstance, ObjectInstance> Explode;

        /// <summary>AS3 <c>loc.prob.check()</c> — a prob room reacts to a door opening.</summary>
        public Action<RoomInstance> CheckProbRoom;

        /// <summary>
        /// AS3 <c>Interact.actOsn()</c> — the "object activates something in the world" hook a
        /// <c>dam</c> falls through to.
        /// </summary>
        public Action<RoomInstance, ObjectInstance> ActivateObject;

        /// <summary>AS3 <c>Interact.shine()</c> — the unlock sparkle.</summary>
        public Action<RoomInstance, ObjectInstance> Shine;

        /// <summary>AS3 <c>Unit.hack(security)</c> — what a <c>hack</c> verb does to a unit.</summary>
        public Action<RoomInstance, ObjectInstance, string> Hack;

        /// <summary>
        /// Resolves the moving-platform states for a room, so the six <c>isMove</c> verbs reach
        /// something. Null is tolerated: <c>stop</c>/<c>move</c>/<c>pop</c>/<c>move1</c>/<c>move2</c>/
        /// <c>move3</c> then do nothing, and the verb is reported as needing one.
        ///
        /// <para><b>A resolver rather than a field, because the states belong to the room and this
        /// command is built before any room is known.</b> <c>RoomInstance.MovingPlatforms</c> is the
        /// lookup; the dispatcher is composed once per scene and asked about many rooms over a session.
        /// </para>
        /// </summary>
        public Func<RoomInstance, MovingPlatformSystem> Platforms;

        /// <summary>
        /// The object's own <c>allAct()</c>, re-entered by <c>open</c>/<c>close</c> when the call did
        /// not come from the broadcast. Null means "no script layer here", which makes the re-entry a
        /// no-op rather than a throw.
        /// </summary>
        public Func<RoomInstance, ObjectInstance, bool> ReenterAllAct;
    }

    /// <summary>
    /// The port of AS3 <c>Interact.command</c> (<c>serv/Interact.as:1697-1818</c>) — the verb dispatch
    /// that a broadcast, a script and a direct interaction all land on.
    ///
    /// <para><b>Three vocabularies, and this is the third one.</b> <c>Script.com</c> is the script
    /// language (<c>Script.as</c>). <c>Obj.command</c>/<c>Box.command</c> is the object vocabulary
    /// (<c>Obj.as:221</c>, <c>Box.as:446</c>) — <c>show</c>, <c>die</c>, and a forward to here. This is
    /// <c>Interact.command</c>, which is what actually moves state: <c>hack</c>, <c>unlock</c>,
    /// <c>open</c>, <c>close</c>, <c>dam</c>, <c>swap</c>, <c>sign</c>, <c>off</c>, the six
    /// <c>isMove</c> verbs, and the <c>red</c>/<c>green</c> signals. They share spellings —
    /// <c>open</c> exists in two of the three — which is exactly why the oracle keeps them in separate
    /// methods and why the port must not merge them either.</para>
    ///
    /// <para><b>The oracle's <c>if</c>s are not <c>else if</c>s, and one of them matters.</b>
    /// <c>Interact.command</c> is a flat run of independent <c>if</c> blocks, so more than one can fire
    /// for a single verb. For every verb but the <c>isMove</c> group that is harmless because the
    /// conditions are mutually exclusive string comparisons. It is <i>not</i> harmless for
    /// <c>open</c> and <c>close</c>, whose last statement re-enters <c>this.allAct()</c> — the cascade
    /// guard <c>param2 != "13"</c> exists precisely because the broadcast calls this method with
    /// <c>"13"</c> and would otherwise bounce the verb back into <c>allAct</c> for ever. Ported
    /// verbatim, including that guard.</para>
    /// </summary>
    public sealed class ObjectVerbCommand
    {
        /// <summary>
        /// The parameter AS3 passes from <c>Location.allAct</c> to suppress the <c>open</c>/<c>close</c>
        /// re-entry (<c>Location.as:2841</c> <c>_loc4_.command(param2,"13")</c>). Named because the
        /// literal appears in three places and its meaning is not guessable from the digits.
        /// </summary>
        public const string BroadcastParam = "13";

        private readonly ObjectVerbHost _host;

        private readonly Action<string> _report;

        public ObjectVerbCommand(ObjectVerbHost host = null)
        {
            _host = host ?? new ObjectVerbHost();
            _report = _host.Report ?? (_ => { });
        }

        /// <summary>
        /// Apply one verb to one object. Returns whether the verb is one this method owns — which is a
        /// different question from "did anything change", and the caller needs the first one to know
        /// whether to keep looking.
        ///
        /// <para><b><paramref name="param"/> is not decoration.</b> It carries the <c>"13"</c> the
        /// broadcast stamps on its calls, and <c>sign</c> reads it as the new sign number
        /// (<c>Interact.as:1760-1763</c> <c>this.sign = int(param2)</c>).</para>
        /// </summary>
        public bool Apply(RoomInstance room, ObjectInstance obj, string verb, string param = null)
        {
            if (obj == null || string.IsNullOrEmpty(verb))
            {
                return false;
            }

            string v = verb.Trim();
            bool fromBroadcast = string.Equals(param, BroadcastParam, StringComparison.Ordinal);

            switch (v)
            {
                case "hack":
                    // Interact.as:1699-1712. Gated on is_hack: a plain object ignores `hack` entirely,
                    // which is what makes `hack_lock`'s sweep over every interactive object in the room
                    // safe — only the hackable ones react.
                    if (!IsHackable(obj))
                    {
                        return true;
                    }

                    ClearMineAndLock(obj);
                    ReenterIfScripted(room, obj, fromBroadcast);
                    _host.Hack?.Invoke(room, obj, string.Empty);
                    _host.Shine?.Invoke(room, obj);
                    return true;

                case "unlock":
                    // Interact.as:1713-1721. Same pair of setAct(101) calls, no is_hack gate — this is
                    // the `unlock` a terminal or a script issues.
                    ClearMineAndLock(obj);
                    _host.Shine?.Invoke(room, obj);
                    return true;

                case "open":
                    // Interact.as:1722-1732.
                    SetOpen(room, obj, true);
                    ReenterIfScripted(room, obj, fromBroadcast);
                    return true;

                case "close":
                    // Interact.as:1733-1743. Note `t_autoClose = 150` in the oracle's dead branch — it
                    // sits inside `if(this.open)` immediately after `this.open = false`, so it can
                    // never run. Not ported; see the note on SetOpen.
                    SetOpen(room, obj, false);
                    ReenterIfScripted(room, obj, fromBroadcast);
                    return true;

                case "dam":
                    // Interact.as:1744-1758. Three arms in priority order: an explosive object
                    // detonates, a knob with action 0 opens and pokes the prob room, and anything else
                    // falls through to actOsn.
                    if (IsExplosive(obj))
                    {
                        _host.Explode?.Invoke(room, obj);
                        return true;
                    }

                    if (IsKnob(obj) && GetInteractionMode(obj) == 0)
                    {
                        SetOpen(room, obj, true);
                        _host.CheckProbRoom?.Invoke(room);
                        return true;
                    }

                    _host.ActivateObject?.Invoke(room, obj);
                    return true;

                case "swap":
                    // Interact.as:1759-1763 — a pure toggle, with no allAct re-entry and no autoClose.
                    SetOpen(room, obj, !IsOpen(obj));
                    return true;

                case "sign":
                    // Interact.as:1764-1767. `this.sign = int(param2)`, which drives the floating sign
                    // bubble drawn in Interact.step. The port carries the number in the runtime state.
                    obj.EnsureStructuredData();
                    if (int.TryParse(
                            param,
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out int sign))
                    {
                        obj.runtimeState.hasSignState = true;
                        obj.runtimeState.signState = sign;
                    }
                    else
                    {
                        obj.runtimeState.hasSignState = false;
                        obj.runtimeState.signState = 0;
                    }

                    return true;

                case "off":
                    // Interact.as:1768-1771 — `this.active = false`, i.e. the object stops offering its
                    // interaction. Distinct from the port's `isActive`, which is the object's own
                    // "am I in the world" flag and is what `off` maps onto here.
                    obj.isActive = false;
                    return true;

                case "red":
                case "green":
                    // Interact.as:1808-1814. Note these sit *after* the isMove block in the oracle and
                    // are not gated by it.
                    _host.Signal?.Invoke(room, obj, v);
                    return true;
            }

            // The six platform verbs, gated on isMove exactly as the oracle gates them.
            if (IsPlatformVerb(v))
            {
                MovingPlatformSystem platforms = _host.Platforms?.Invoke(room);

                if (platforms == null)
                {
                    ReportOnce(
                        "platform-verbs",
                        "`" + v + "` reached an object but the room has no MovingPlatformSystem, so " +
                        "nothing moved. A button carrying allact='" + v + "' needs the room's platform " +
                        "states — see RoomInstance.MovingPlatforms.");
                    return true;
                }

                if (!platforms.TryCommand(obj, v))
                {
                    // The object is not a platform. In the oracle that is silent — `if(this.isMove)`
                    // simply does not run — and it is the common case, because a broadcast to an
                    // `allid` tag hits doors and knobs as well as the platform.
                    return true;
                }

                return true;
            }

            return false;
        }

        /// <summary>Whether <paramref name="verb"/> is one of the six <c>isMove</c> verbs.</summary>
        public static bool IsPlatformVerb(string verb)
        {
            switch (verb)
            {
                case "stop":
                case "move":
                case "pop":
                case "move1":
                case "move2":
                case "move3":
                    return true;

                default:
                    return false;
            }
        }

        // ── helpers ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>setAct("open", n)</c> (<c>Interact.as:845-880</c>) plus the <c>open</c>/<c>close</c>
        /// field writes.
        ///
        /// <para><b>The <c>autoClose</c> arm is not ported, and it is worth saying why.</b>
        /// <c>this.t_autoClose = this.autoClose</c> arms a countdown that <c>Interact.step</c> decrements
        /// and, at 1, turns into <c>command("close")</c> (<c>:630-637</c>). Six placements in the shipped
        /// rooms author <c>autoclose</c> — all of them <c>knop2</c> buttons that also carry a
        /// <c>&lt;move&gt;</c> node. Without a countdown the button stays open instead of springing back,
        /// so this is a real gap rather than a no-op; it is reported rather than silently dropped,
        /// because "the button stays out" is a symptom a play-test will see and a report will explain.
        /// </para>
        /// </summary>
        private void SetOpen(RoomInstance room, ObjectInstance obj, bool open)
        {
            obj.EnsureStructuredData();
            obj.runtimeState.isOpen = open;

            if (HasAutoClose(obj))
            {
                ReportOnce(
                    "autoclose",
                    "This object authors `autoclose` but the port has no countdown for it, so it stays " +
                    "in its new state instead of springing back. First seen on '" +
                    obj.GetResolvedDefinitionId() + "'. See Interact.as:630-637.");
            }

            _host.StampDoorTiles?.Invoke(room, obj, open);

            if (IsKnob(obj))
            {
                _host.SetVisualState?.Invoke(room, obj, open ? "open" : "close");
            }

            if (open)
            {
                // Interact.as:865-868 — opening clears the mine/lock state.
                obj.runtimeState.hasMineValue = false;
                obj.runtimeState.mineValue = 0;
                obj.runtimeState.hasLockValue = false;
                obj.runtimeState.lockValue = 0f;
            }

            _host.CheckProbRoom?.Invoke(room);
        }

        private void ClearMineAndLock(ObjectInstance obj)
        {
            obj.EnsureStructuredData();
            obj.runtimeState.hasMineValue = false;
            obj.runtimeState.mineValue = 0;
            obj.runtimeState.hasLockValue = false;
            obj.runtimeState.lockValue = 0f;
        }

        /// <summary>
        /// AS3's <c>if(Boolean(this.allact) &amp;&amp; param2 != "13") this.allAct()</c>
        /// (<c>Interact.as:1728</c>, <c>:1740</c>).
        /// </summary>
        private void ReenterIfScripted(RoomInstance room, ObjectInstance obj, bool fromBroadcast)
        {
            if (fromBroadcast || string.IsNullOrEmpty(obj.GetAllAct()))
            {
                return;
            }

            _host.ReenterAllAct?.Invoke(room, obj);
        }

        private static bool IsOpen(ObjectInstance obj)
        {
            obj.EnsureStructuredData();
            return obj.runtimeState.isOpen;
        }

        /// <summary>AS3 <c>Interact.is_hack</c>, set from the definition's <c>hack</c> attribute (<c>Interact.as:283-286</c>).</summary>
        private static bool IsHackable(ObjectInstance obj)
        {
            return ReadDefinitionInt(obj, "hack") > 0;
        }

        /// <summary>AS3 <c>Interact.expl</c>.</summary>
        private static bool IsExplosive(ObjectInstance obj)
        {
            obj.EnsureStructuredData();
            return obj.runtimeState.isExploded
                   || MapObjectDataUtility.HasAttribute(obj.attributes, "expl")
                   || ReadDefinitionInt(obj, "expl") > 0;
        }

        /// <summary>AS3 <c>Interact.knop</c>, set from the placement's <c>knop</c> attribute (<c>Interact.as:408-411</c>).</summary>
        private static bool IsKnob(ObjectInstance obj)
        {
            obj.EnsureStructuredData();
            return !string.IsNullOrEmpty(MapObjectDataUtility.GetAttribute(obj.attributes, "knop", string.Empty));
        }

        /// <summary>AS3 <c>Interact.autoClose</c>, from <c>@autoclose</c> on the definition then the placement (<c>Interact.as:318</c>, <c>:377</c>).</summary>
        private static bool HasAutoClose(ObjectInstance obj)
        {
            if (obj.definition != null && obj.definition.autoClose)
            {
                return true;
            }

            if (ReadDefinitionInt(obj, "autoclose") > 0)
            {
                return true;
            }

            return int.TryParse(
                obj.GetAttribute("autoclose", string.Empty),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int placed) && placed > 0;
        }

        /// <summary>AS3 <c>Interact.action</c> — the <c>inter</c> value, placement first then definition (<c>Interact.as:292</c>, <c>:397-400</c>).</summary>
        private static int GetInteractionMode(ObjectInstance obj)
        {
            string placed = obj.GetAttribute("inter", string.Empty);
            if (int.TryParse(placed, out int placedValue))
            {
                return placedValue;
            }

            return ReadDefinitionInt(obj, "inter");
        }

        private static int ReadDefinitionInt(ObjectInstance obj, string key)
        {
            if (obj.definition == null)
            {
                return 0;
            }

            return int.TryParse(
                obj.definition.GetAttribute(key, string.Empty),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int value)
                ? value
                : 0;
        }

        private readonly System.Collections.Generic.HashSet<string> _reported =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        private void ReportOnce(string id, string message)
        {
            if (_reported.Add(id))
            {
                _report("[ObjectVerb] " + message);
            }
        }
    }

    /// <summary>
    /// The port of AS3 <c>Area.command</c> (<c>loc/Area.as:257-286</c>) — a <b>different</b> verb set from
    /// <see cref="ObjectVerbCommand"/>, for a different class.
    ///
    /// <para><b>An area does not forward to <c>Interact.command</c>, and that is why this type has to
    /// exist.</b> <c>Box.command</c> ends with <c>if(inter) inter.command(param1, param2)</c>
    /// (<c>Box.as:446-458</c>) and <c>UnitTurret.command</c> has its own four verbs — but
    /// <c>Area.command</c> overrides without calling <c>inter</c> at all. So an area ignores
    /// <c>open</c>, <c>close</c>, <c>swap</c>, <c>move3</c> and every other object verb, and it does
    /// <b>not</b> re-enter its own <c>allAct</c>. It answers exactly four: <c>onoff</c>, <c>off</c>,
    /// <c>on</c> and <c>dam</c>.</para>
    ///
    /// <para><b>Which makes the four authored area <c>allact</c> values inert.</b> The shipped rooms place
    /// four areas with an <c>allact</c> — <c>open</c>×3, <c>close</c>×1 — and <c>activate</c>×1 on an
    /// area in the survey. None of those is <c>onoff</c>/<c>off</c>/<c>on</c>/<c>dam</c>, so in the oracle
    /// every one of them broadcasts a verb its targets ignore. That is not a bug to fix: it is what the
    /// data does, and a port that "helpfully" routed area verbs into the object handler would make an
    /// area start opening doors — a gameplay invention.</para>
    /// </summary>
    public static class AreaVerbCommand
    {
        /// <summary>
        /// Apply one verb to one area. Returns whether the verb is one an area owns.
        ///
        /// <para><b>The <c>setLift</c> and alpha lines run for every verb, not just the four.</b> The
        /// oracle's <c>Area.command</c> is a flat run of <c>if</c>s with the <c>setLift</c>/alpha pair
        /// sitting outside them, so <c>command("open")</c> on an area still refreshes its lift and
        /// opacity. Modelled here as "any call refreshes presentation", which is the same thing without
        /// pretending the refresh is a verb.</para>
        /// </summary>
        public static bool Apply(
            RoomInstance room,
            ObjectInstance area,
            string verb,
            string param,
            Action<RoomInstance, ObjectInstance> refreshPresentation = null,
            Action<RoomInstance, ObjectInstance, int> damageTiles = null)
        {
            if (area == null || string.IsNullOrEmpty(verb))
            {
                return false;
            }

            string v = verb.Trim();
            bool owned = v == "onoff" || v == "off" || v == "on" || v == "dam";

            if (v == "onoff")
            {
                area.isActive = !area.isActive;
            }
            else if (v == "off")
            {
                area.isActive = false;
            }
            else if (v == "on")
            {
                area.isActive = true;
            }
            else if (v == "dam")
            {
                int amount = int.TryParse(
                    param,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int parsed)
                    ? parsed
                    : 0;

                damageTiles?.Invoke(room, area, amount);
            }

            refreshPresentation?.Invoke(room, area);

            return owned;
        }
    }
}
