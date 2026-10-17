using System;
using System.Collections.Generic;

namespace PFE.Systems.Loot
{
    /// <summary>
    /// The per-run <c>@limit</c> counter store — AS3 <c>Game.limits</c> with
    /// <c>Game.getLimit</c>/<c>Game.addLimit</c> (<c>Game.as:29,681-708</c>).
    ///
    /// <para><b>Why it is a class and not a field on the campaign.</b> <see cref="ILootLimitStore"/> is
    /// what <c>LootRoller</c> already asks for, and the roller is deliberately free of save-state
    /// dependencies. This is the one implementation of that contract: it holds the counters, and it reads
    /// and writes the story-trigger table through two delegates so the campaign stays the owner of the
    /// triggers and this stays testable with no campaign at all.</para>
    ///
    /// <para><b>AS3's falsy zero is load-bearing.</b> Every read in the oracle is
    /// <c>if(this.limits[param1])</c>, which is false for a stored <c>0</c> as well as for an absent key.
    /// So a counter that reaches 0 reads as "never seen", and <c>getLimit</c> then seeds itself from the
    /// trigger of the same name (<c>Game.as:687-691</c>). Modelling the table as "absent vs 0" instead
    /// would change that seeding, so the zero test is reproduced rather than tidied.</para>
    /// </summary>
    public sealed class LootLimitStore : ILootLimitStore
    {
        private readonly Dictionary<string, int> _limits = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Func<string, int> _readTrigger;
        private readonly Action<string, int> _writeTrigger;

        /// <param name="readTrigger">
        /// Reads the story trigger of the same name (<c>Game.triggers</c>). <c>null</c> disables the seed
        /// arm, which is what a host with no campaign wants.
        /// </param>
        /// <param name="writeTrigger">
        /// Writes the story trigger — <c>addLimit(key, 2)</c>'s destination (<c>Game.as:709-719</c>).
        /// <c>null</c> makes that arm a no-op, reported by <see cref="AddLimit"/>'s return value.
        /// </param>
        public LootLimitStore(Func<string, int> readTrigger = null, Action<string, int> writeTrigger = null)
        {
            _readTrigger = readTrigger;
            _writeTrigger = writeTrigger;
        }

        /// <summary>AS3 <c>Game.getLimit(id)</c> (<c>Game.as:681-694</c>).</summary>
        public int GetLimit(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;

            if (_limits.TryGetValue(key, out int stored) && stored != 0)
            {
                return stored;
            }

            int fromTrigger = _readTrigger?.Invoke(key) ?? 0;
            if (fromTrigger != 0)
            {
                _limits[key] = fromTrigger;
                return fromTrigger;
            }

            _limits[key] = 0;
            return 0;
        }

        /// <summary>
        /// AS3 <c>Game.addLimit(id, n)</c> (<c>Game.as:696-720</c>). <paramref name="delta"/> <c>1</c>
        /// increments the limit counter; <c>2</c> increments the <i>trigger</i> instead. Any other value
        /// does nothing, as in the oracle.
        /// </summary>
        public void AddLimit(string key, int delta)
        {
            if (string.IsNullOrEmpty(key)) return;

            if (delta == 1)
            {
                _limits[key] = _limits.TryGetValue(key, out int stored) && stored != 0 ? stored + 1 : 1;
                return;
            }

            if (delta == 2)
            {
                int current = _readTrigger?.Invoke(key) ?? 0;
                _writeTrigger?.Invoke(key, current != 0 ? current + 1 : 1);
            }
        }

        /// <summary>
        /// The raw counter for a key, without the trigger seed. Used by a save payload and by a debug
        /// readout; <see cref="GetLimit"/> is what the roller must call.
        /// </summary>
        public bool TryPeekRaw(string key, out int value)
        {
            if (string.IsNullOrEmpty(key))
            {
                value = 0;
                return false;
            }

            return _limits.TryGetValue(key, out value);
        }

        /// <summary>How many distinct keys have been touched. Telemetry for a debug readout.</summary>
        public int Count => _limits.Count;

        /// <summary>Restore a saved counter table. AS3 writes <c>limits</c> from the save object.</summary>
        public void Load(IEnumerable<KeyValuePair<string, int>> pairs)
        {
            _limits.Clear();
            if (pairs == null) return;

            foreach (KeyValuePair<string, int> pair in pairs)
            {
                if (!string.IsNullOrEmpty(pair.Key)) _limits[pair.Key] = pair.Value;
            }
        }

        /// <summary>Drop every counter. Used by <c>gotoNextLevel</c>'s land rebuild in tests, and by tests.</summary>
        public void Clear() => _limits.Clear();
    }
}
