using PFE.Data.Definitions;
using PFE.Entities.Units;

namespace PFE.Entities.Enemies.Bosses
{
    /// <summary>
    /// Which <see cref="UnitDefinition"/> row carries each of the raider leader's <b>two bodies</b>.
    ///
    /// <para><b>The bug this exists for.</b> <c>UnitBossRaider.as:54-61</c> builds one of two display
    /// objects from the placement's <c>tr</c>:</para>
    ///
    /// <code>
    /// if(this.tr == 2) { vis = new visualRaiderBoss2(); }
    /// else             { vis = new visualRaiderBoss();  }
    /// </code>
    ///
    /// <para>…and the two are genuinely different art, not two exports of one clip: disjoint symbol-id
    /// sets (<c>2080,2081,2084-2091,2094</c> against <c>2025,2026,2029-2036,2041</c>), Boss-1 all-black
    /// with an orange mane and tail, Boss-2 orange-brown. The port imported <c>visualRaiderBoss2</c> only
    /// — <c>bossraider.asset</c> names it in <c>visualClassName</c> — so every raider boss was drawn as
    /// the flamer.</para>
    ///
    /// <para><b>Which one the player actually meets.</b> <c>tr</c> defaults to <b>1</b>
    /// (<c>UnitBossRaider.as:12</c>, <c>public var tr:int = 1</c>), so a placement that authors no
    /// <c>tr</c> is the <b>carbine</b> variant. The reachable raider-boss room is
    /// <c>rooms_raiders/room_8_1</c> (<c>uid="buch"</c>), and it authors none — so the one fight the
    /// player meets is the all-black gunner, and it was drawn in the flamer's orange. The hub's
    /// <c>rbl</c> boss doors (<c>RoomsProb.as:1091</c>, <c>:6193</c>) do author <c>tr='2'</c>, which is
    /// why the wrong art survived: the flamer variant was correct all along.</para>
    ///
    /// <para><b>Why the row ids look inverted, and why they are not renamed.</b> <c>bossraider</c> is
    /// the id rooms place (<c>AllData.as:193</c>), so it must keep that name; the carbine body had to
    /// be imported beside it and took the next free id, <c>bossraider1</c>. So the suffix counts
    /// <b><c>tr</c></b>, not the import order: <c>bossraider1</c> ↔ <c>tr == 1</c> ↔
    /// <c>visualRaiderBoss</c>, and the unsuffixed row is the <c>tr == 2</c> flamer. Renaming either
    /// would break every room that places <c>bossraider</c>.</para>
    ///
    /// <para><b>The two rows differ only in art.</b> <c>bossraider1.asset</c> is a byte copy of
    /// <c>bossraider.asset</c> with five fields replaced — <c>m_Name</c>, <c>id</c>,
    /// <c>visualClassName</c>, <c>sprite</c> and the 210-entry <c>spriteSheet</c>. Every stat, row and
    /// animation is identical because the oracle's two bodies share one <c>&lt;unit id='bossraider'&gt;</c>
    /// row; only the clip changes.</para>
    /// </summary>
    public static class BossRaiderVariants
    {
        /// <summary>
        /// <c>tr != 2</c> — <c>visualRaiderBoss</c>, the all-black body with the carbine
        /// (<c>UnitBossRaider.as:56-60</c>, <c>:71-75</c>).
        /// </summary>
        public const string CarbineVisualDefinitionId = "bossraider1";

        /// <summary>
        /// <c>tr == 2</c> — <c>visualRaiderBoss2</c>, the orange body with the flamer
        /// (<c>:54-56</c>, <c>:76-80</c>). This is the row rooms place.
        /// </summary>
        public const string FlamerVisualDefinitionId = "bossraider";

        /// <summary>
        /// The definition id for a <c>tr</c> value. <b>Only <c>2</c> selects the flamer</b> — the
        /// oracle's test is <c>tr == 2</c> against <c>tr == 1</c> in two separate <c>if</c>s, and
        /// <c>tr</c> is an <c>int</c> from an XML attribute, so any other authored value (0, 3, a
        /// non-numeric that never parsed) lands on the carbine body exactly as it does there.
        /// </summary>
        public static string VisualDefinitionId(int variant) =>
            variant == 2 ? FlamerVisualDefinitionId : CarbineVisualDefinitionId;

        /// <summary>
        /// Resolve <paramref name="variant"/>'s body, or <c>false</c> when the row is not in the
        /// provider — the caller then keeps whatever art it already had, and says so.
        /// </summary>
        public static bool TryResolveVisual(int variant, IUnitDefinitionProvider provider,
                                            out UnitDefinition definition)
        {
            definition = null;
            return provider != null &&
                   provider.TryGetUnit(VisualDefinitionId(variant), out definition) &&
                   definition != null;
        }
    }
}
