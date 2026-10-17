using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// What <c>UnitNPC.command</c> does with a verb — the branch ladder of
    /// <c>UnitNPC.as:321-381</c>, as data.
    ///
    /// <para><b>Why this is split out of the controller.</b> The controller is a <c>MonoBehaviour</c>
    /// and this project's offline harness cannot construct one — <c>EnemyBrainTests</c> and
    /// <c>UnitMotorReHomeTests</c> have never run outside the editor for exactly that reason. The verb
    /// ladder is the part that can be wrong in a way nothing reports (a mis-spelled <c>case</c> silently
    /// queues instead of acting, and the oracle's own fallback <i>is</i> a queue), so it lives here where
    /// a fixture can reach it.</para>
    /// </summary>
    public enum UnitNpcVerbKind
    {
        /// <summary>Not a <c>UnitNPC</c> verb. The caller offers it to the base <c>Unit.command</c>.</summary>
        Unknown = 0,

        /// <summary><c>UnitNPC.as:325-328</c> — <c>hide()</c>.</summary>
        Hide = 1,

        /// <summary><c>UnitNPC.as:329-340</c> — reveal and fade in.</summary>
        Show = 2,

        /// <summary><c>UnitNPC.as:341-350</c> — <c>vis.osn.gotoAndStop(2)</c>.</summary>
        OpenEyes = 3,

        /// <summary><c>UnitNPC.as:351-364</c> — the objective icon.</summary>
        Sign = 4,

        /// <summary><c>UnitNPC.as:365-369</c> — <c>t_replic = 0; replic("vse")</c>.</summary>
        ReplicVse = 5,

        /// <summary><c>UnitNPC.as:370-373</c> — <c>targNPC.rep = int(val)</c>.</summary>
        Rep = 6,

        /// <summary>
        /// <c>UnitNPC.as:374-380</c> — <b>the <c>else</c></b>. Every other verb is pushed onto
        /// <c>que</c> and run later by <c>control()</c>, one per <c>wait</c> expiry.
        ///
        /// <para>This is a real branch, not a failure: <c>fly</c> and <c>ai</c> reach the NPC only this
        /// way, and the queue is what makes <c>fly surfCalam 40:7</c> a movement the unit performs over
        /// several frames rather than a teleport. It is also why an unknown verb is <i>not</i> reported
        /// as an error here — the oracle drops it silently in <c>analiz</c>, and inventing a warning
        /// would fire on verbs the oracle accepts.</para>
        /// </summary>
        Queue = 7
    }

    /// <summary>
    /// One queued verb — AS3's <c>{com, val}</c> object (<c>UnitNPC.as:376-379</c>).
    /// </summary>
    public readonly struct QueuedNpcVerb
    {
        public QueuedNpcVerb(string command, string value)
        {
            Command = command ?? string.Empty;
            Value = value;
        }

        /// <summary>AS3 <c>com</c>.</summary>
        public string Command { get; }

        /// <summary>AS3 <c>val</c>, or <c>null</c> when the action carried none.</summary>
        public string Value { get; }

        public override string ToString() => Value == null ? Command : $"{Command} {Value}";
    }

    /// <summary>
    /// The pure rules behind <c>UnitNPC</c> and <c>fe.serv.NPC</c> — verb classification, the fly
    /// target's tile arithmetic, the persona's hidden-state table, and the landing rule.
    ///
    /// <para><b>Every constant here is a citation.</b> The numbers are the oracle's, including the two
    /// that look like mistakes and are not: the fly target adds one to the X tile and none to the Y
    /// (<c>UnitNPC.as:405</c>), and the "arrived" radius is compared against the <i>squared</i> distance
    /// (<c>:538-548</c>). Both are preserved verbatim, because a port that quietly "fixed" either would
    /// put Calamity a tile away from where the original puts him.</para>
    /// </summary>
    public static class UnitNpcRules
    {
        // ── The queue's own timings (UnitNPC.as:402-410) ────────────────────────────────────────

        /// <summary>AS3 <c>this.wait = 1000</c> when a <c>fly</c> is queued — one verb every 1000 frames
        /// until the flight ends.</summary>
        public const int FlyWaitFrames = 1000;

        /// <summary>AS3 <c>if(_loc2_ &lt; 40 * 40)</c> — the squared "close enough to brake" radius.</summary>
        public const float FlyBrakeRadiusSquared = 40f * 40f;

        /// <summary>AS3 <c>if(_loc2_ &lt; 5 * 5)</c> — the squared "arrived" radius.</summary>
        public const float FlyArriveRadiusSquared = 5f * 5f;

        /// <summary>AS3 <c>dx *= 0.85</c> / <c>dy *= 0.85</c> inside the brake radius.</summary>
        public const float FlyBrakePerFrame = 0.85f;

        /// <summary>AS3 <c>this.t_float += 0.243</c> — the hover bob's phase step.</summary>
        public const float FlyFloatPhaseStep = 0.243f;

        /// <summary>
        /// AS3 <c>if(isFly &amp;&amp; dy &lt; 5) dy += 1;</c> — <c>UnitNPC.as:594-599</c>, the agro arm's
        /// <c>else</c>. With nothing to shoot, a flying NPC sinks one px/frame until it touches the
        /// floor, which is what eventually reaches the landing block below.
        ///
        /// <para><b>Magnitude only — the sign lives at the call site.</b> AS3's <c>dy</c> is
        /// down-positive; the port's <c>As3VelocityPixelsPerFrame</c> is Unity-signed, so the caller
        /// <b>subtracts</b> this value. Keeping the constant positive means it reads as "one pixel per
        /// frame of descent" rather than as a signed velocity, which is the thing that made a literal
        /// transcription climb instead of sink.</para>
        /// </summary>
        public const float AgroDescentPerFrame = 1f;

        /// <summary>
        /// AS3 <c>dy &lt; 5</c> — the ceiling on the descent above. Preserved as the oracle's own
        /// number: it means the sink stops adding once the unit is already falling faster than 5 px a
        /// frame, so a knocked-down NPC is not accelerated further by its own idle.
        ///
        /// <para>The call site compares <c>dy &gt; -AgroDescentClamp</c>, the mirrored form of AS3's
        /// <c>dy &lt; 5</c>.</para>
        /// </summary>
        public const float AgroDescentClamp = 5f;

        /// <summary>AS3 <c>this.floatY = Math.cos(this.t_float) * 0.5</c>.</summary>
        public const float FlyFloatAmplitude = 0.5f;

        /// <summary>AS3 <c>vis.alpha += 0.05</c> (<c>UnitNPC.as:506-513</c>) — the reveal fade rate.</summary>
        public const float RevealAlphaPerFrame = 0.05f;

        // ── AS3's celX/celY — the aim point every held weapon reads ────────────────────────────────
        //
        // `UnitNPC.control()` rewrites these two numbers every frame and `Weapon.actions()` aims along
        // `atan2(celY - weaponY, |celX - weaponX| * storona)`. The port published an aim target only on a
        // firing tick and took it from the target's transform position — its FEET — which tilted the
        // barrel down by a body height. These four constants are the oracle's two branches.

        /// <summary>
        /// AS3 <c>celX = X + storona * 100</c> (<c>UnitNPC.as:530</c>) — how far in front of itself an
        /// NPC aims when it has nothing to shoot. Only the <i>direction</i> matters, because the weapon
        /// rotates toward the point; the distance is the oracle's and is kept so the two readings cannot
        /// drift apart.
        /// </summary>
        public const float IdleAimForwardPixels = 100f;

        /// <summary>
        /// AS3 <c>celY = Y - 10</c> (<c>UnitNPC.as:531</c>) — the idle aim point, ten pixels above the
        /// unit's own feet. In AS3's down-positive space that is a subtraction; in the port it is
        /// <b>plus</b> ten pixels of height.
        /// </summary>
        public const float IdleAimAboveFeetPixels = 10f;

        /// <summary>
        /// AS3 <c>celX = param1.X + param1.scX / 4 * param1.storona</c> (<c>Unit.as:4707</c>) — the
        /// target's box centre nudged a quarter-width along its own facing. A quarter, not a half: the
        /// oracle aims at the near edge of the torso rather than its axis.
        /// </summary>
        public const float TargetAimWidthFraction = 0.25f;

        /// <summary>
        /// AS3 <c>celY = param1.Y - param1.scY / 2</c> (<c>Unit.as:4708</c>) — the target's box
        /// <b>centre</b>, which is the whole of the aiming fix: the barrel angle is measured from the
        /// shooter's hands, so aiming at the target's feet points the gun below the target.
        /// </summary>
        public const float TargetAimHeightFraction = 0.5f;

        /// <summary>AS3 <c>this.t_ref = 16</c> (<c>UnitNPC.as:303</c>) — the re-interaction lockout.</summary>
        public const int ReferenceCooldownFrames = 16;

        /// <summary>
        /// <c>UnitNPC.command</c>'s branch ladder (<c>UnitNPC.as:321-381</c>), minus the
        /// <c>super.command</c> call the caller makes first.
        /// </summary>
        public static UnitNpcVerbKind Classify(string verb)
        {
            if (string.IsNullOrEmpty(verb))
            {
                return UnitNpcVerbKind.Unknown;
            }

            switch (verb.Trim().ToLowerInvariant())
            {
                case "hide": return UnitNpcVerbKind.Hide;
                case "show": return UnitNpcVerbKind.Show;
                case "openeyes": return UnitNpcVerbKind.OpenEyes;
                case "sign": return UnitNpcVerbKind.Sign;
                case "replicvse": return UnitNpcVerbKind.ReplicVse;
                case "rep": return UnitNpcVerbKind.Rep;
                default: return UnitNpcVerbKind.Queue;
            }
        }

        /// <summary>
        /// The destination of a queued <c>fly</c> — AS3 <c>UnitNPC.as:402-410</c>.
        ///
        /// <code>
        /// _loc2_ = param1.val.split(":");
        /// this.cx = (int(_loc2_[0]) + 1) * World.tileX;
        /// this.cy = int(_loc2_[1]) * World.tileY;
        /// </code>
        ///
        /// <para><b>The <c>+1</c> on X and not on Y is the oracle's, and it is kept.</b> AS3's
        /// <c>tileX</c>/<c>tileY</c> are the tile pitch in pixels (40), and a placement's <c>x</c> is a
        /// tile index; the asymmetry means <c>fly 40:7</c> lands one tile right of where a symmetric
        /// reading would put it. "Fixing" it would move Calamity relative to every other unit in the
        /// game, so it stays — recorded here so the next reader does not have to rediscover it.</para>
        ///
        /// <para><b>Non-numeric becomes 0, not an error.</b> AS3's <c>int("junk")</c> is <c>0</c>, so a
        /// malformed target flies to <c>(1 * tileX, 0)</c> — the top-left of the room. Preserved,
        /// because the alternative (skip the command) leaves the unit stuck at its spawn with no
        /// signal either.</para>
        /// </summary>
        /// <param name="value">The authored <c>val</c>, e.g. <c>"40:7"</c>.</param>
        /// <param name="tileX">AS3 <c>World.tileX</c> — the tile pitch in pixels (40).</param>
        /// <param name="tileY">AS3 <c>World.tileY</c>.</param>
        public static Vector2 ParseFlyTarget(string value, float tileX, float tileY)
        {
            int tileIndexX = 0;
            int tileIndexY = 0;

            if (!string.IsNullOrEmpty(value))
            {
                string[] parts = value.Split(':');
                if (parts.Length > 0)
                {
                    int.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Integer,
                                 System.Globalization.CultureInfo.InvariantCulture, out tileIndexX);
                }

                if (parts.Length > 1)
                {
                    int.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Integer,
                                 System.Globalization.CultureInfo.InvariantCulture, out tileIndexY);
                }
            }

            return new Vector2((tileIndexX + 1) * tileX, tileIndexY * tileY);
        }

        /// <summary>
        /// The hover bob's vertical offset for a given phase — AS3 <c>Math.cos(t_float) * 0.5</c>
        /// (<c>UnitNPC.as:523-524</c>). In AS3 px/frame; the caller scales.
        /// </summary>
        public static float FloatOffset(float phase) => Mathf.Cos(phase) * FlyFloatAmplitude;

        /// <summary>
        /// Whether a flying NPC has arrived — AS3 <c>UnitNPC.as:538-548</c>.
        ///
        /// <para><b>The comparison is against the <i>squared</i> distance.</b> AS3 computes
        /// <c>_loc2_ = dvig.x*dvig.x + dvig.y*dvig.y</c> and then tests <c>_loc2_ &lt; 5*5</c>, so the
        /// real arrival radius is <b>5 px</b> (and the brake radius 40 px) — a value the oracle reuses
        /// without taking a square root. Taking the root here would be "cleaner" and would change the
        /// distance at which the flight stops, so it does not happen.</para>
        /// </summary>
        /// <param name="deltaX">AS3 <c>dvig.x</c> — target minus current, in pixels.</param>
        /// <param name="deltaY">AS3 <c>dvig.y</c>.</param>
        public static bool HasArrived(float deltaX, float deltaY)
            => deltaX * deltaX + deltaY * deltaY < FlyArriveRadiusSquared;

        /// <summary>Whether a flying NPC is inside the brake radius — <c>UnitNPC.as:538</c>.</summary>
        public static bool ShouldBrake(float deltaX, float deltaY)
            => deltaX * deltaX + deltaY * deltaY < FlyBrakeRadiusSquared;

        /// <summary>
        /// The steering acceleration toward the fly target — AS3 <c>norma(dvig, accel)</c>
        /// (<c>Obj.as:248-257</c>) followed by <c>dx += dvig.x; dy += dvig.y</c>.
        ///
        /// <para><c>norma</c> scales the vector to <paramref name="acceleration"/> <b>only when it is
        /// longer</b>; a shorter vector is left alone. That asymmetry is the oracle's and it is what
        /// makes the approach settle instead of oscillating.</para>
        /// </summary>
        /// <param name="deltaX">AS3 <c>dvig.x</c>.</param>
        /// <param name="deltaY">AS3 <c>dvig.y</c>.</param>
        /// <param name="acceleration">AS3 <c>accel</c> — px per frame squared.</param>
        public static Vector2 SteeringAcceleration(float deltaX, float deltaY, float acceleration)
        {
            float squared = deltaX * deltaX + deltaY * deltaY;
            if (squared <= acceleration * acceleration)
            {
                return new Vector2(deltaX, deltaY);
            }

            float length = Mathf.Sqrt(squared);
            if (length <= 0f)
            {
                return Vector2.zero;
            }

            float scale = acceleration / length;
            return new Vector2(deltaX * scale, deltaY * scale);
        }

        // ── fe.serv.NPC — the persona tables ────────────────────────────────────────────────────

        /// <summary>
        /// <c>NPC.init()</c>'s hidden rule (<c>NPC.as:110-127</c>).
        ///
        /// <para><b>Only Calamity has one that depends on <c>rep</c>, and it is the reason he is
        /// invisible when the surf room loads.</b> <c>NPC.as:112-117</c>:
        /// <c>if(id == "calam") { if(rep == 0 || trig("rbl_visited") &gt; 0) hidden = true; }</c>. So he
        /// starts hidden until the script's <c>show</c> reveals him — and once the player has visited the
        /// camp, he stays hidden for the rest of the game.</para>
        /// </summary>
        /// <param name="npcId">The persona id.</param>
        /// <param name="rep">AS3 <c>NPC.rep</c>.</param>
        /// <param name="rblVisited">AS3 <c>trig("rbl_visited") &gt; 0</c>.</param>
        public static bool InitialHidden(string npcId, int rep, bool rblVisited)
        {
            if (!string.Equals(npcId, "calam", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return rep == 0 || rblVisited;
        }

        /// <summary>
        /// <c>NPC.init()</c>'s <c>aiTip</c> rule (<c>NPC.as:118-121</c>): a Calamity who has already
        /// fought (<c>rep == 1</c>) starts <c>agro</c>.
        /// </summary>
        /// <returns>The <c>aiTip</c> to apply, or <c>null</c> when this persona has no opinion.</returns>
        public static string InitialAiTip(string npcId, int rep)
        {
            if (string.Equals(npcId, "calam", StringComparison.OrdinalIgnoreCase) && rep == 1)
            {
                return "agro";
            }

            return null;
        }

        /// <summary>
        /// <c>NPC.refresh()</c>'s hidden rule (<c>NPC.as:134-195</c>) — the table every other persona
        /// uses.
        ///
        /// <para><b>The comparisons are deliberately not normalised.</b> AS3 compares
        /// <c>triggers[name]</c>, which is <c>undefined</c> when unset, and <c>undefined</c> compares
        /// false with <c>&gt;</c>, <c>&lt;</c> and <c>==</c> but <i>true</i> with <c>!=</c>. So
        /// <c>calam3</c>'s <c>trig("storm") != 1</c> is <b>true</b> for an unset trigger (hidden) while
        /// <c>askari3</c>'s <c>trig("storm") &lt; 2</c> is <b>false</b> (visible). Treating "unset" as 0
        /// would flip <c>calam3</c> — hence the nullable input.</para>
        /// </summary>
        /// <param name="npcId">The persona id.</param>
        /// <param name="rep">AS3 <c>NPC.rep</c>.</param>
        /// <param name="triggerValue">
        /// AS3 <c>game.triggers[name]</c> as a nullable int — <c>null</c> for <c>undefined</c>. The
        /// caller passes the raw value, not a coerced one.
        /// </param>
        /// <returns>The new <c>hidden</c> value, or <c>null</c> when this persona has no rule.</returns>
        public static bool? RefreshHidden(string npcId, int rep, Func<string, int?> triggerValue)
        {
            if (string.IsNullOrEmpty(npcId) || triggerValue == null)
            {
                return null;
            }

            switch (npcId.ToLowerInvariant())
            {
                case "calam":
                    // :136-142 is not a hidden rule — it re-arms the AI. Returned as no-opinion.
                    return null;

                case "calam2": return triggerValue("storm") > 0;                       // :143-146
                case "calam3": return triggerValue("storm") != 1;                      // :147-150
                case "calam4":                                                         // :151-154
                {
                    int? storm = triggerValue("storm");
                    return storm != 2 && storm != 3;
                }

                case "calam5": return triggerValue("storm") != 3;                      // :155-158
                case "steel2": return triggerValue("mbase_visited") > 0;               // :159-162
                case "steel": return triggerValue("storm") == 5;                       // :163-166
                case "askari": return triggerValue("story_ranger") > 0;                // :167-170
                case "askari2":                                                        // :171-174
                {
                    int? ranger = triggerValue("story_ranger");
                    return (ranger.HasValue && ranger.Value <= 0) || triggerValue("storm") > 0;
                }

                case "observer": return triggerValue("observer") != 1;                 // :179-182
                case "observer2": return triggerValue("storm") != 2;                   // :183-186
                case "askari3":                                                        // :187-190
                {
                    int? storm = triggerValue("storm");
                    return storm.HasValue && storm.Value < 2;
                }

                case "mentor": return triggerValue("theend") > 0;                      // :191-194
                default: return null;
            }
        }

        /// <summary>
        /// <c>NPC.landing()</c> (<c>NPC.as:197-203</c>) — the <c>rep</c> a landing sets.
        /// </summary>
        /// <returns>The new <c>rep</c>, or <c>null</c> when this persona has no landing rule.</returns>
        public static int? LandingRep(string npcId)
        {
            return string.Equals(npcId, "calam", StringComparison.OrdinalIgnoreCase) ? 1 : (int?)null;
        }

        /// <summary>
        /// <c>NPC.refresh()</c>'s <c>aiTip</c> rule (<c>NPC.as:136-142</c>): a Calamity who has not yet
        /// fought has his AI cleared, so a reloaded room does not leave him mid-hunt.
        /// </summary>
        /// <returns>The <c>aiTip</c> to apply, or <c>null</c> when this persona has no opinion.</returns>
        public static string RefreshAiTip(string npcId, int rep)
        {
            return string.Equals(npcId, "calam", StringComparison.OrdinalIgnoreCase) && rep == 0
                ? string.Empty
                : null;
        }

        /// <summary>
        /// The <c>zzz</c> particle gate — <c>NPC.refresh()</c>'s <c>patient</c> arm
        /// (<c>NPC.as:175-178</c>): <c>zzzGen = rep == 2</c>.
        /// </summary>
        public static bool SleepParticlesFor(string npcId, int rep)
            => string.Equals(npcId, "patient", StringComparison.OrdinalIgnoreCase) && rep == 2;

        /// <summary>
        /// The queued verbs <c>analiz</c> understands — <c>UnitNPC.as:383-442</c>. Anything else is
        /// consumed and dropped, which is the oracle's behaviour for an unknown verb.
        ///
        /// <para>Exposed so a fixture can assert the set rather than trusting the switch's spelling: a
        /// mis-cased verb would fall into <c>analiz</c>'s silent void, and the symptom would be "the NPC
        /// ignored the command" with nothing in the log.</para>
        /// </summary>
        public static IReadOnlyList<string> AnalizableVerbs { get; } = new[]
        {
            "tell", "check", "ai", "fly", "rem", "turn", "mater"
        };

        /// <summary>Whether <c>analiz</c> would act on this verb (<c>UnitNPC.as:383-442</c>).</summary>
        public static bool IsAnalizable(string verb)
        {
            if (string.IsNullOrEmpty(verb)) return false;

            string normalized = verb.Trim().ToLowerInvariant();
            for (int i = 0; i < AnalizableVerbs.Count; i++)
            {
                if (string.Equals(AnalizableVerbs[i], normalized, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// <c>analiz</c>'s <c>turn</c> arm (<c>UnitNPC.as:415-430</c>) as a facing delta.
        ///
        /// <code>
        /// if(val == "0") storona = -storona; else if(val == "-1") storona = -1; else storona = 1;
        /// </code>
        /// </summary>
        /// <returns>The new facing: <c>1</c>, <c>-1</c>, or <c>0</c> for "flip whatever it is".</returns>
        public static int TurnFacing(string value)
        {
            if (value == "0") return 0;
            if (value == "-1") return -1;
            return 1;
        }

        // ── The persona clip's own rules (UnitNPC.as:164-174, 243-281, 341-350) ──────────────────

        /// <summary>
        /// AS3 <c>this.t_anim = Math.random() * 200 + 150</c> (<c>UnitNPC.as:252</c>) — the shortest
        /// gap between two fidgets, in display frames.
        /// </summary>
        public const int FidgetMinFrames = 150;

        /// <summary>
        /// The width of the fidget interval above <see cref="FidgetMinFrames"/>: AS3's
        /// <c>Math.random() * 200</c>. Kept as its own constant because the port's roll is
        /// <c>[0,1)</c> × this + <see cref="FidgetMinFrames"/>, and a reader comparing against the
        /// oracle needs both numbers.
        /// </summary>
        public const int FidgetIntervalFrames = 200;

        /// <summary>
        /// AS3 <c>vis.osn.gotoAndStop(2)</c> in the <c>openEyes</c> verb (<c>UnitNPC.as:345</c>) — a
        /// timeline frame number, not a list index.
        /// </summary>
        public const int OpenEyesFrame = 2;

        /// <summary>
        /// AS3 <c>vis.osn.gotoAndStop(1)</c> — the fallback at <c>UnitNPC.as:172</c> and <c>:486</c>
        /// when the persona has no <c>stay</c> label to stop on.
        /// </summary>
        public const int FirstFrame = 1;

        /// <summary>
        /// AS3 <c>this.t_anim = Math.random() * 200 + 150</c> (<c>UnitNPC.as:252</c>) — the next fidget
        /// delay, given a roll in <c>[0,1)</c>.
        /// </summary>
        /// <remarks>
        /// <b>The truncation is the oracle's.</b> AS3 assigns the product straight to an <c>int</c>
        /// field, so <c>349.7</c> becomes <c>349</c>. A port that rounded would fire the fidget up to
        /// one frame late on half the rolls; over a 150-frame interval that is invisible, but the rule
        /// is free to state exactly.
        /// </remarks>
        public static int NextFidgetDelay(float roll01)
        {
            float clamped = roll01 < 0f ? 0f : (roll01 > 1f ? 1f : roll01);
            return FidgetMinFrames + (int)(clamped * FidgetIntervalFrames);
        }

        /// <summary>
        /// AS3 <c>_loc1_ = Math.floor(Math.random() * 2 + 1); vis.osn.gotoAndPlay("move" + _loc1_)</c>
        /// (<c>UnitNPC.as:253-256</c>) — the fidget label for a roll in <c>[0,1)</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>The roll is consumed even when the label does not exist, and that is faithful.</b>
        /// The oracle computes <c>_loc1_</c> <i>before</i> the <c>try</c>, so a persona with no
        /// <c>move1</c>/<c>move2</c> — <c>visualCalam</c> is one, its labels being
        /// <c>stay,trot,run,polz,jump,laz,plav,punch,kick,die,derg,fly</c> — still advances the random
        /// stream before its <c>gotoAndPlay</c> throws. A port that skipped the roll when the label was
        /// absent would desync every later roll on that unit.</para>
        ///
        /// <para><b>Only <c>1</c> and <c>2</c> are ever produced.</b> <c>floor(rnd * 2 + 1)</c> is
        /// <c>1</c> for <c>rnd &lt; 0.5</c> and <c>2</c> otherwise; <c>rnd</c> is never exactly
        /// <c>1</c>, so <c>3</c> is unreachable.</para>
        /// </remarks>
        public static string FidgetLabel(float roll01)
        {
            int index = roll01 < 0.5f ? 1 : 2;
            return "move" + index;
        }
    }
}
