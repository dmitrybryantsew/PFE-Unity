using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Units
{
    /// <summary>
    /// One live speech bubble — the port's stand-in for an AS3 <c>replic</c> particle.
    /// </summary>
    public struct UnitBark
    {
        /// <summary>Where the bubble hangs, in world units (already offset and jittered).</summary>
        public Vector3 Anchor;

        /// <summary>The prepared line.</summary>
        public string Text;

        /// <summary>Seconds since it appeared.</summary>
        public float Age;

        /// <summary>Seconds it lives — 100 frames at the oracle's 30 Hz.</summary>
        public float Lifetime;
    }

    /// <summary>
    /// The queue of barks currently on screen — AS3's <c>Emitter.emit("replic", …)</c> payload, minus
    /// the particle system.
    ///
    /// <para><b>Why not a particle.</b> The port's particle pipeline draws sprite frames out of
    /// <c>ParticleDefinitions</c>, and the two rows this feature needs are already there —
    /// <c>&lt;part id='replic' vis='visReplic' …&gt;</c> and <c>replic2</c> — but their visual is a
    /// <b>text field</b>, not a sprite: <c>visReplic</c> is a 404×21 plate of yellow text and the
    /// emitter's own code is what writes into it
    /// (<c>if((this.id == "replic" || this.id == "replic2") &amp;&amp; param4.txt) _loc6_.vis.text.text.text = param4.txt;</c>,
    /// <c>Emitter.as:371-374</c>). The port has no sprite frame for either row, so emitting them today
    /// draws nothing. A text queue plus an IMGUI overlay is the same information through a renderer
    /// that actually exists.</para>
    ///
    /// <para><b>The geometry is the oracle's, in world units.</b> <c>Y - 110</c> px above the unit's
    /// feet for an enemy and <c>Y - 90</c> for the player, jittered by <c>±ry/2</c> (<c>ry: 50</c> and
    /// <c>20</c> respectively — <c>Unit.as:4846-4849</c>, <c>UnitPlayer.as:4986-4989</c>). The port's
    /// world is 100 px per unit (<c>CameraFollow.pixelsPerUnit</c>), so 110 px is
    /// <see cref="EnemyOffsetWorld"/>.</para>
    ///
    /// <para><b>The lifetime is 100 frames, and the fade is the last nine.</b> <c>minliv='100'</c> with
    /// <c>rliv='0'</c> on the <c>replic</c> row, and <c>Part</c>'s alpha rule is
    /// <c>if(isAlph &amp;&amp; liv &lt; 9) vis.alpha = liv / 10</c> (<c>Part.as:171-173</c>). At the
    /// port's 30 Hz that is 3.33 s of full-strength text and a 0.3 s fade.</para>
    /// </summary>
    public static class UnitBarkFeed
    {
        /// <summary>
        /// How many bubbles can be live at once. AS3 has no cap — a <c>replic</c> row can be cast
        /// without limit — but a room's worth of enemies barking over each other is unreadable, and
        /// this queue is read by an IMGUI loop that draws nine labels per bubble.
        /// </summary>
        public const int Capacity = 32;

        /// <summary>The oracle's 30 Hz frame — <c>SimClock</c>'s canonical rate.</summary>
        public const float OracleTicksPerSecond = 30f;

        /// <summary><c>minliv='100'</c> → 100 / 30 s.</summary>
        public const float LifetimeSeconds = 100f / OracleTicksPerSecond;

        /// <summary>The fade window — <c>liv &lt; 9</c> (<c>Part.as:171</c>).</summary>
        public const float FadeFrames = 9f;

        /// <summary>The fade's divisor — <c>vis.alpha = liv / 10</c>.</summary>
        public const float FadeDivisor = 10f;

        /// <summary><c>Y - 110</c> px above an enemy's feet, at 100 px per world unit.</summary>
        public const float EnemyOffsetWorld = 1.10f;

        /// <summary><c>Y - 90</c> px above the player's feet.</summary>
        public const float PlayerOffsetWorld = 0.90f;

        /// <summary>The enemy bubble's <c>ry: 50</c> spread, halved — <c>(rand - 0.5) * ry</c>.</summary>
        public const float EnemyJitterWorld = 0.25f;

        /// <summary>The player bubble's <c>ry: 20</c> spread, halved.</summary>
        public const float PlayerJitterWorld = 0.10f;

        private static readonly UnitBark[] Buffer = new UnitBark[Capacity];

        private static int _count;

        /// <summary>Bubbles currently alive.</summary>
        public static int Count => _count;

        /// <summary>The live bubble at <paramref name="index"/>, oldest first.</summary>
        public static UnitBark At(int index) => Buffer[index];

        /// <summary>
        /// Add a bubble above <paramref name="feet"/>.
        /// </summary>
        /// <param name="feet">The unit's transform position — the port anchors units at their feet.</param>
        /// <param name="text">A non-empty line; an empty one is ignored rather than drawn blank.</param>
        /// <param name="player">True for <c>replic2</c>'s shorter offset and tighter jitter.</param>
        /// <param name="jitter01">A <c>[0,1)</c> draw; the oracle's <c>Math.random()</c>.</param>
        /// <remarks>
        /// <b>Full queue drops the oldest.</b> AS3 would keep casting and let the layer overflow; a
        /// readable screen is worth more than the eleventh simultaneous taunt, and the alternative —
        /// refusing the new one — would make a fresh bark invisible exactly when a crowd is shouting.
        /// </remarks>
        public static void Emit(Vector3 feet, string text, bool player, float jitter01)
        {
            if (string.IsNullOrEmpty(text)) return;

            float offset = player ? PlayerOffsetWorld : EnemyOffsetWorld;
            float jitter = player ? PlayerJitterWorld : EnemyJitterWorld;

            // (rand - 0.5) * ry, with ry halved into world units — the oracle's own expression.
            float spread = (Mathf.Clamp01(jitter01) - 0.5f) * 2f * jitter;

            var bark = new UnitBark
            {
                Anchor = new Vector3(feet.x, feet.y + offset + spread, feet.z),
                Text = text,
                Age = 0f,
                Lifetime = LifetimeSeconds
            };

            if (_count == Capacity)
            {
                // Shift down by one: the oldest bubble is the one that has been readable longest.
                System.Array.Copy(Buffer, 1, Buffer, 0, Capacity - 1);
                Buffer[Capacity - 1] = bark;
                return;
            }

            Buffer[_count++] = bark;
        }

        /// <summary>
        /// Age every bubble and drop the expired ones, compacting in place.
        /// </summary>
        /// <remarks>
        /// Called from the overlay's <c>Update</c> rather than from a sim tick: a bubble is a
        /// presentation artefact, and it must keep fading while the game is paused for a dialogue —
        /// which is exactly when the player is reading the screen.
        /// </remarks>
        public static void Advance(float deltaSeconds)
        {
            if (_count == 0 || deltaSeconds <= 0f) return;

            int write = 0;
            for (int read = 0; read < _count; read++)
            {
                UnitBark bark = Buffer[read];
                bark.Age += deltaSeconds;
                if (bark.Age >= bark.Lifetime) continue;

                Buffer[write++] = bark;
            }

            // Clear the tail so a stale struct cannot be read through At() after Count drops.
            for (int i = write; i < _count; i++) Buffer[i] = default;
            _count = write;
        }

        /// <summary>
        /// The bubble's alpha — <c>vis.alpha = liv / 10</c> over the last nine frames, 1 before that
        /// (<c>Part.as:171-180</c>).
        /// </summary>
        public static float Alpha(in UnitBark bark)
        {
            float remainingFrames = (bark.Lifetime - bark.Age) * OracleTicksPerSecond;
            if (remainingFrames >= FadeFrames) return 1f;
            if (remainingFrames <= 0f) return 0f;
            return remainingFrames / FadeDivisor;
        }

        /// <summary>Drop every bubble — a room change or a new game.</summary>
        public static void Clear()
        {
            for (int i = 0; i < _count; i++) Buffer[i] = default;
            _count = 0;
        }

        /// <summary>
        /// A snapshot for a caller that needs a stable list (a fixture, or an overlay that draws while
        /// <see cref="Advance"/> could run). Copies into <paramref name="into"/> without allocating.
        /// </summary>
        public static void Snapshot(List<UnitBark> into)
        {
            if (into == null) return;
            into.Clear();
            for (int i = 0; i < _count; i++) into.Add(Buffer[i]);
        }
    }
}
