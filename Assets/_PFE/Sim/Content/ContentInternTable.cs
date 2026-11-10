using System;
using System.Collections.Generic;

namespace PFE.Sim.Content
{
    /// <summary>
    /// The interned, content-versioned id table that <c>SimUnitState.DefinitionId</c> and
    /// <c>SimProjectileState.DefinitionId</c> address — the thing that let Phase 3.3 emit those fields
    /// as 0.
    ///
    /// <para><b>Why a table and not a hash.</b> The record's contract is "an interned,
    /// content-versioned id", and the tempting shortcut — <c>(ushort)(fnv1a(id) &amp; 0xFFFF)</c> — is
    /// stable but collision-prone: at a few hundred weapons a 16-bit space collides roughly 0.1% of
    /// the time, and a collision means a plasma round renders and damages as a pistol, intermittently,
    /// on one host. That is an invented value that passes review. An index into a sorted table cannot
    /// collide at all.</para>
    ///
    /// <para><b>What "content-versioned" actually means here, stated plainly.</b> The keys are a
    /// function of the content set, not a persistent identity. Two peers with the same content build
    /// the same table regardless of load order, mod registration order or dictionary iteration order,
    /// because the assignment is a sort. Two peers with <i>different</i> content get different keys —
    /// and that is correct, because they must be rejected by the protocol/content version rather than
    /// silently reconciled. Adding a definition that sorts before an existing one therefore renumbers
    /// it; a build that renumbers is a new content version, and the wire must not mix the two.</para>
    ///
    /// <para><b>Key 0 is reserved.</b> <c>Uninterned</c> means "this record's content id is not in the
    /// table", which is a state a receiver must be able to see rather than mistake for the first
    /// definition. A hash-based scheme cannot express it without stealing a real value.</para>
    ///
    /// <para><b>Ordinal ordering, not culture ordering.</b> <c>StringComparer.Ordinal</c> compares
    /// bytes; the default culture-sensitive comparer depends on the machine's locale, so the same
    /// content would produce different tables on two hosts and every key would silently mean something
    /// else. That is the one way this class can be wrong while looking correct.</para>
    /// </summary>
    public sealed class ContentInternTable
    {
        /// <summary>Reserved key meaning "not interned". Never assigned to a definition.</summary>
        public const ushort Uninterned = 0;

        /// <summary>Byte order, locale-independent. See the class remarks for why this is not optional.</summary>
        private static readonly StringComparer Ordering = StringComparer.Ordinal;

        private static readonly ContentInternTable EmptyTable = Build(Array.Empty<string>());

        private readonly Dictionary<string, ushort> _byId;
        private readonly string[] _byKey;

        private ContentInternTable(Dictionary<string, ushort> byId, string[] byKey)
        {
            _byId = byId;
            _byKey = byKey;
        }

        /// <summary>The table with no entries. Usable, not null — an unconfigured build must still answer.</summary>
        public static ContentInternTable Empty => EmptyTable;

        /// <summary>How many definitions are interned (excludes <see cref="Uninterned"/>).</summary>
        public int Count => _byKey.Length - 1;

        /// <summary>
        /// Build the table from a content id sequence. Order of <paramref name="ids"/> is irrelevant —
        /// that is the whole point — and duplicates, nulls and empty strings are dropped rather than
        /// shifting every later key.
        /// </summary>
        public static ContentInternTable Build(IEnumerable<string> ids)
        {
            var distinct = new List<string>();

            if (ids != null)
            {
                var seen = new HashSet<string>(Ordering);

                foreach (string id in ids)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    if (seen.Add(id)) distinct.Add(id);
                }
            }

            distinct.Sort(Ordering);

            var byId = new Dictionary<string, ushort>(distinct.Count, Ordering);
            var byKey = new string[distinct.Count + 1];

            for (int i = 0; i < distinct.Count; i++)
            {
                ushort key = unchecked((ushort)(i + 1));

                // A 65 534-entry content set would overflow the ushort space. Refusing here is better
                // than wrapping onto key 1 and making the first definition mean two things.
                if (key == Uninterned)
                {
                    throw new InvalidOperationException(
                        "ContentInternTable cannot hold more than " + ushort.MaxValue +
                        " definitions; the ushort key space would wrap onto key 0.");
                }

                byId[distinct[i]] = key;
                byKey[key] = distinct[i];
            }

            return new ContentInternTable(byId, byKey);
        }

        /// <summary>Key for a content id, or false when the id is not in this table.</summary>
        public bool TryGetKey(string contentId, out ushort key)
        {
            if (!string.IsNullOrEmpty(contentId) && _byId.TryGetValue(contentId, out key))
            {
                return true;
            }

            key = Uninterned;
            return false;
        }

        /// <summary>
        /// Key for a content id, or <see cref="Uninterned"/>. The projecting path: a missing id must
        /// produce a visible 0, not an exception inside a per-tick loop.
        /// </summary>
        public ushort GetKeyOrDefault(string contentId)
        {
            return TryGetKey(contentId, out ushort key) ? key : Uninterned;
        }

        /// <summary>The content id for a key, or null for <see cref="Uninterned"/> / an out-of-range key.</summary>
        public string GetContentId(ushort key)
        {
            return key < _byKey.Length ? _byKey[key] : null;
        }

        /// <summary>
        /// Whether two tables intern the same ids to the same keys. The check a peer handshake needs,
        /// and the assertion a determinism fixture makes.
        /// </summary>
        public bool SameContentAs(ContentInternTable other)
        {
            if (other == null || other._byKey.Length != _byKey.Length) return false;

            for (int i = 0; i < _byKey.Length; i++)
            {
                if (!string.Equals(_byKey[i], other._byKey[i], StringComparison.Ordinal)) return false;
            }

            return true;
        }
    }
}
