namespace PFE.Systems.Map
{
    /// <summary>
    /// The durability of an authored <b>door / barricade box</b> — AS3's <c>Box.hp</c> and
    /// <c>Box.thre</c> as a weapon applies them.
    ///
    /// <para><b>Why the numbers live here rather than on the presenter.</b> A door is not a tile in
    /// the port's data model — it is an <c>ObjectInstance</c> whose durability is authored on the
    /// <i>definition</i> row (<c>MapObjectDefinition.hitPoints</c> / <c>.threshold</c>, imported from
    /// <c>hp=</c> / <c>thre=</c>). The rule that turns those two numbers plus a weapon's destroy
    /// value into "ignored / chipped / destroyed" is pure arithmetic with no Unity type in it, so it
    /// is kept out of the MonoBehaviour and pinned by fixtures that need no engine.</para>
    ///
    /// <para><b>The oracle, in full.</b> A weapon reaches a door through the tile the door stands
    /// on, not through the box: <c>Bullet.run()</c> tests the cell it occupies
    /// (<c>weapon/Bullet.as:476</c>) and calls <c>loc.hitTile(tile, this.destroy, …)</c>
    /// (<c>:489</c>), which is <c>Tile.udar(destroy)</c> (<c>Tile.as:340-348</c>):</para>
    /// <code>
    ///   if (indestruct || thre &gt; param1) return false;
    ///   hp -= param1;
    /// </code>
    /// <para>and then, when <c>hp &lt;= 0</c>, <c>param1.door.die()</c> (<c>Location.as:2523-2537</c>).
    /// <c>Box.initDoor()</c> is what put the box's own <c>hp</c>/<c>thre</c> onto those tiles in the
    /// first place (<c>Box.as:670-671</c>), so "the door's durability" and "the tile's durability"
    /// are one number in the oracle. The port keeps that number on the box and applies the same
    /// arithmetic; see <c>DoorPropPresenter.DamageDoor</c>.</para>
    ///
    /// <para><b>Deliberately the compare form, not <c>Box.damage()</c>'s subtract form.</b> AS3 has
    /// two different door-damage rules. <c>Tile.udar()</c> <i>ignores</i> a hit whose destroy value
    /// is below <c>thre</c>; <c>Box.damage()</c> (<c>Box.as:776-787</c>) <i>subtracts</i> <c>thre</c>
    /// from the damage first and only then applies it, and is reached only from
    /// <c>Box.udarBullet</c> — i.e. for a <c>bulPlayer</c> box, or an explosion that hits the box
    /// directly. A weapon fired at a door takes the tile path, so the compare form is the one that
    /// decides. It is also the rule the port already uses for walls
    /// (<c>TileData.TakeDamage</c>), which is what keeps a door and a wall agreeing about what a
    /// given weapon does.</para>
    /// </summary>
    public static class DoorDestructionMath
    {
        /// <summary>
        /// AS3 <c>Box.hp</c>'s own initialiser — <c>public var hp:Number = 100</c>
        /// (<c>Box.as:56</c>).
        ///
        /// <para><b>Needed because the importer cannot tell "absent" from "zero".</b>
        /// <c>MapObjectDefinitionImporter</c> writes <c>GetFloatAttr("hp", 0f)</c>, so a definition
        /// row that authors no <c>hp</c> at all lands on disk as <c>hitPoints: 0</c> — the same value
        /// a deliberate <c>hp='0'</c> would produce. AS3 distinguishes them (<c>node.@hp.length()</c>
        /// gates the assignment), and its answer for the absent case is this constant. Resolving the
        /// zero to 100 is therefore the oracle's behaviour for the only case the port can be in, not
        /// an invented number. Every one of the 21 shipped <c>door=</c> rows authors <c>hp</c>, so
        /// this is reachable only from a hand-built object or a new definition.</para>
        /// </summary>
        public const float DefaultHitPoints = 100f;

        /// <summary>
        /// AS3 <c>Box.thre</c>'s initialiser — <c>public var thre:Number = 0</c> (<c>Box.as:58</c>),
        /// which is also what the importer's <c>GetFloatAttr("thre", …)</c> falls back to.
        /// </summary>
        public const float DefaultThreshold = 0f;

        /// <summary>
        /// What AS3 assigns to <b>both</b> <c>hp</c> and <c>thre</c> when a placed node carries
        /// <c>indestruct</c> — <c>Box.as:285-289</c>. A single sentinel rather than a boolean,
        /// because the oracle's own "cannot be broken" is expressed as numbers, and
        /// <see cref="IgnoresDamage"/> then refuses every weapon in the game without a special
        /// case. 10000 exceeds the largest <c>destroy</c> any shipped weapon carries.
        /// </summary>
        public const float IndestructibleValue = 10000f;

        /// <summary>
        /// The door's starting hit points: <see cref="IndestructibleValue"/> for an indestructible
        /// placement, otherwise the authored value, or <see cref="DefaultHitPoints"/> when the
        /// authored value is absent (see the constant's note on why 0 means "absent" here).
        /// </summary>
        public static float ResolveHitPoints(float authoredHitPoints, bool indestructible)
        {
            if (indestructible) return IndestructibleValue;
            return authoredHitPoints > 0f ? authoredHitPoints : DefaultHitPoints;
        }

        /// <summary>
        /// The door's damage threshold: <see cref="IndestructibleValue"/> for an indestructible
        /// placement, otherwise the authored value. Unlike hit points there is no "absent" case to
        /// rescue — AS3's default is 0, which is also what the importer writes.
        /// </summary>
        public static float ResolveThreshold(float authoredThreshold, bool indestructible)
        {
            if (indestructible) return IndestructibleValue;
            return authoredThreshold > DefaultThreshold ? authoredThreshold : DefaultThreshold;
        }

        /// <summary>
        /// AS3 <c>Tile.udar()</c>'s guard, <c>thre &gt; param1</c>: a hit below the threshold is
        /// ignored <b>entirely</b> — there is no chip damage, so a pistol can never wear a steel
        /// door down. A hit <i>equal</i> to the threshold is not ignored (<c>&gt;</c>, not
        /// <c>&gt;=</c>), which is the corner the shipped <c>door2</c> sits on with
        /// <c>thre='100'</c>.
        ///
        /// <para>The <c>threshold &gt; 0</c> term mirrors <c>TileData.TakeDamage</c>, the port's
        /// existing wall rule, so the two cannot disagree about a zero threshold.</para>
        /// </summary>
        public static bool IgnoresDamage(float threshold, float damage)
        {
            return threshold > DefaultThreshold && damage < threshold;
        }

        /// <summary>AS3 <c>hp -= param1</c>, in place on the caller's copy.</summary>
        public static float ApplyDamage(float hitPoints, float damage)
        {
            return hitPoints - damage;
        }

        /// <summary>AS3's <c>hp &lt;= 0</c> — the condition that runs <c>param1.door.die()</c>.</summary>
        public static bool IsDestroyed(float hitPoints)
        {
            return hitPoints <= 0f;
        }
    }
}
