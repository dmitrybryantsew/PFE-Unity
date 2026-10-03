using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// One landed (or evaded) hit, as the floating-damage overlay needs to see it: where it happened,
    /// how big it was, and how long ago.
    /// </summary>
    /// <remarks>
    /// A struct, and it holds no colour — the view decides what a crit looks like. Keeping the visual
    /// choice out of the data is what lets <see cref="DamageEventFeed"/> be tested without a renderer.
    /// </remarks>
    public readonly struct DamageNumber
    {
        /// <summary>Impact point, in world space — the same point <c>DamageDealtMessage.position</c> carries.</summary>
        public readonly Vector3 Position;

        /// <summary>HP damage actually applied. <c>0</c> on a miss.</summary>
        public readonly float Amount;

        public readonly bool IsCritical;
        public readonly bool IsMiss;

        /// <summary>Seconds since the hit. Grows; the entry is dropped past the feed's lifetime.</summary>
        public readonly float Age;

        public readonly string CustomText;
        public readonly Color? CustomColor;

        public DamageNumber(Vector3 position, float amount, bool isCritical, bool isMiss, float age)
            : this(position, amount, isCritical, isMiss, age, null, null)
        {
        }

        public DamageNumber(Vector3 position, float amount, bool isCritical, bool isMiss, float age, string customText, Color? customColor)
        {
            Position = position;
            Amount = amount;
            IsCritical = isCritical;
            IsMiss = isMiss;
            Age = age;
            CustomText = customText;
            CustomColor = customColor;
        }
    }

    /// <summary>
    /// A small bounded log of recent hits, written by <c>DamageSystem</c> and drained by the
    /// floating-damage overlay.
    ///
    /// <para><b>Why a feed and not a direct call into the overlay.</b> <c>DamageSystem</c> lives in the
    /// simulation and must not know a debug overlay exists; the overlay self-bootstraps from a scene-load
    /// hook and has no DI container to resolve a subscriber from. A tiny push/pull buffer decouples them
    /// in both directions: the simulation writes four values, the view reads what it likes, and either
    /// can be absent. This is also why the buffer is bounded and drops the oldest entry rather than
    /// growing — a diagnostic that allocates without limit while you hold down the fire button is worse
    /// than the bug it was opened to find.</para>
    ///
    /// <para><b>The default instance is shared; tests should not use it.</b> <see cref="Default"/> is a
    /// process-wide singleton so the overlay can read it without wiring, which means state written by
    /// one test would leak into the next. Every test constructs its own.</para>
    /// </summary>
    public sealed class DamageEventFeed
    {
        /// <summary>Enough for a burst of automatic fire without dropping the frame's first hits.</summary>
        public const int DefaultCapacity = 64;

        /// <summary>
        /// How long a number lives. AS3's own damage numbers are short-lived; this is long enough to
        /// read a burst as separate figures and short enough that a held trigger does not silt up.
        /// </summary>
        public const float DefaultLifetime = 0.9f;

        /// <summary>How far a number rises over its whole life, in world units.</summary>
        public const float RiseDistance = 0.85f;

        /// <summary>The process-wide feed the overlay reads. See the class remarks.</summary>
        public static DamageEventFeed Default { get; } = new DamageEventFeed();

        private readonly List<DamageNumber> _live = new List<DamageNumber>();
        private readonly int _capacity;
        private readonly float _lifetime;

        public DamageEventFeed(int capacity = DefaultCapacity, float lifetime = DefaultLifetime)
        {
            _capacity = Mathf.Max(1, capacity);
            _lifetime = Mathf.Max(0.01f, lifetime);
        }

        /// <summary>Entries currently held, including ones already past their lifetime.</summary>
        public int Count => _live.Count;

        public float Lifetime => _lifetime;

        /// <summary>
        /// Record a hit. <paramref name="amount"/> is HP damage as applied, so a fully-absorbed hit
        /// shows <c>0</c> — which is a finding, not a reason to skip the entry: "the shot connected and
        /// did nothing" is exactly what an armour bug looks like.
        /// </summary>
        public void Report(Vector3 position, float amount, bool isCritical, bool isMiss)
        {
            if (_live.Count >= _capacity)
            {
                // Drop the oldest rather than refuse the newest: under sustained fire the recent hits
                // are the ones being diagnosed.
                _live.RemoveAt(0);
            }

            _live.Add(new DamageNumber(position, amount, isCritical, isMiss, 0f));
        }

        /// <summary>
        /// Record an arbitrary floating text notification (e.g. "+100xp", AS3 Pers.as:980 numbEmit).
        /// </summary>
        public void ReportText(Vector3 position, string text, Color color)
        {
            if (_live.Count >= _capacity)
            {
                _live.RemoveAt(0);
            }

            _live.Add(new DamageNumber(position, 0f, isCritical: false, isMiss: false, age: 0f, customText: text, customColor: color));
        }

        /// <summary>
        /// Age every entry by <paramref name="deltaTime"/>, drop the expired, and copy the survivors
        /// into <paramref name="into"/>. Returns how many survived.
        /// </summary>
        /// <remarks>
        /// Ageing happens here, once per view refresh, rather than by storing an absolute birth time —
        /// so a paused or backgrounded game does not expire the whole buffer at once, and a test can
        /// advance time without a clock.
        /// </remarks>
        public int AdvanceAndSnapshot(float deltaTime, List<DamageNumber> into)
        {
            if (into == null) return 0;

            into.Clear();

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                float age = _live[i].Age + deltaTime;

                if (age >= _lifetime)
                {
                    _live.RemoveAt(i);
                    continue;
                }

                _live[i] = new DamageNumber(
                    _live[i].Position, _live[i].Amount, _live[i].IsCritical, _live[i].IsMiss, age,
                    _live[i].CustomText, _live[i].CustomColor);
            }

            for (int i = 0; i < _live.Count; i++)
            {
                into.Add(_live[i]);
            }

            return into.Count;
        }

        public void Clear() => _live.Clear();

        // ── Pure view maths ──────────────────────────────────────────────────

        /// <summary>
        /// Vertical offset at <paramref name="age"/>, in world units — linear, so a burst reads as a
        /// column of figures rather than a clump. Deliberately not eased: an eased curve makes two hits
        /// 50 ms apart overlap for most of their life.
        /// </summary>
        public static float RiseOffset(float age, float lifetime)
        {
            if (lifetime <= 0f) return 0f;

            float t = Mathf.Clamp01(age / lifetime);

            return t * RiseDistance;
        }

        /// <summary>
        /// Opacity at <paramref name="age"/>. Holds full for the first half of the life so the number is
        /// legible while it matters, then fades. A number that fades from frame one is unreadable at
        /// exactly the moment it is most useful.
        /// </summary>
        public static float Alpha(float age, float lifetime)
        {
            if (lifetime <= 0f) return 0f;

            float t = Mathf.Clamp01(age / lifetime);

            return t < 0.5f ? 1f : 1f - (t - 0.5f) * 2f;
        }

        /// <summary>
        /// The text for one entry. A miss reads <c>MISS</c> rather than <c>0</c> on purpose: the two
        /// have different causes — one is the evasion roll, the other is armour absorbing everything —
        /// and a shared <c>0</c> would hide which happened.
        /// </summary>
        public static string TextFor(in DamageNumber number)
        {
            if (!string.IsNullOrEmpty(number.CustomText)) return number.CustomText;
            if (number.IsMiss) return "MISS";

            // Rounded up, so a 0.4-damage graze does not render as "0" and read as a miss.
            int amount = Mathf.CeilToInt(number.Amount);

            return number.IsCritical ? amount + "!" : amount.ToString();
        }
    }
}
