namespace PFE.Systems.Physics
{
    /// <summary>
    /// AS3's <b>throw window</b> on a prop — <c>Box.t_throw</c> (<c>fe/loc/Box.as:94</c>), the counter
    /// that makes a crate still leaving the thrower's hands harmless — as pure numbers.
    ///
    /// <para><b>Both values are <c>static readonly</c>, not <c>const</c>, and that is deliberate.</b> A
    /// <c>const</c> is baked into every assembly that mentions it at compile time, so a fixture
    /// asserting <c>Assert.AreEqual(2, PlayerThrowTicks)</c> would compare two copies of a literal that
    /// was frozen when the <i>test</i> assembly was built — the mutant "change 2 to 0" would then be
    /// invisible unless the test assembly itself were rebuilt, and a mutation control would report it
    /// as a survivor. As fields, the values are read from the shipping assembly at run time, which is
    /// the only way a test can observe them at all.</para>
    ///
    /// <para><b>This is not the same flag as <c>Box.isThrow</c>, and the port used to treat it as one.</b>
    /// The oracle has two independent fields on every box:</para>
    /// <list type="table">
    /// <item>
    ///   <term><c>isThrow</c></term>
    ///   <description>Set by every thrower — the player (<c>UnitPlayer.as:1870</c>), an alicorn
    ///   (<c>UnitAlicorn.as:1238</c>) and the alicorn boss (<c>UnitBossAlicorn.as:693</c>). Its only
    ///   readers are the collision predicates: a thrown box passes <b>over</b> shelves instead of
    ///   landing on them (<c>Box.as:1049</c>, <c>:1270</c>). It is cleared by the first wall, floor or
    ///   ceiling contact (<c>:935</c>, <c>:1005</c>, <c>:1029</c>, <c>:1105</c>, <c>:1137</c>) — never by
    ///   a timer. It has <b>no effect on damage</b>.</description>
    /// </item>
    /// <item>
    ///   <term><c>t_throw</c></term>
    ///   <description>The damage grace. Read once, in <c>attDrop</c> (<c>Box.as:928</c>): while it is
    ///   positive the box hands the unit it overlaps <c>neujaz = 12</c> instead of calling
    ///   <c>udarBox</c>. Counted down by one at the end of every box step (<c>:646</c>).</description>
    /// </item>
    /// </list>
    ///
    /// <para><b>Only the player sets <c>t_throw</c>.</b> <c>UnitPlayer.as:1871</c> is the single writer
    /// in the whole oracle — <c>(this.teleObj as Box).t_throw = 2;</c>. An alicorn's <c>throwTele</c>
    /// (<c>UnitAlicorn.as:1205-1246</c>) and the boss's (<c>UnitBossAlicorn.as:657-696</c>) set
    /// <c>isThrow</c> and <b>nothing else</b>, so a crate an alicorn throws hurts the first thing it
    /// touches, immediately. Two ticks of grace exist for one reason: the player throws from inside the
    /// crate's own bounding box, so without them the crate would damage the thrower.</para>
    ///
    /// <para><b>Why this is a type rather than two <c>const</c>s at the call sites.</b> The port's
    /// original model had one flag (<c>isThrown</c>) standing for both facts, so every telekinetic
    /// release — enemy or player — granted a grace that lasted until the prop happened to touch a wall.
    /// An alicorn-thrown crate could therefore never damage anybody, which is the report that found
    /// this. Keeping the numbers here, and the gate in <c>UnitController.SweepPropImpacts</c>, makes
    /// "who threw it" the only question a call site has to answer.</para>
    /// </summary>
    public static class PropThrowGrace
    {
        /// <summary>
        /// AS3 <c>UnitPlayer.as:1871</c> — <c>t_throw = 2</c>. Two <b>prop steps</b>, not two seconds:
        /// the counter is decremented once per box step (<c>Box.as:646</c>) and is never scaled by a
        /// delta time, which is why this is an integer here.
        /// </summary>
        public static readonly int PlayerThrowTicks = 2;

        /// <summary>
        /// <c>UnitAlicorn.as:1238</c> / <c>UnitBossAlicorn.as:693</c> — these throwers write
        /// <c>isThrow</c> and leave <c>t_throw</c> at its field default of <c>0</c>. Not "a very short
        /// grace": no grace at all, on the first overlapping unit, on the same tick it is released.
        /// </summary>
        public static readonly int EnemyThrowTicks = 0;

        /// <summary>Whether a prop with this many ticks left still refuses to damage.</summary>
        public static bool IsActive(int throwGraceTicks) => throwGraceTicks > 0;

        /// <summary>
        /// One prop step of the countdown — <c>Box.as:646</c>. Clamps at zero so a prop that is never
        /// stepped again cannot drift negative and wrap into a positive window.
        /// </summary>
        public static int Tick(int throwGraceTicks) => throwGraceTicks > 0 ? throwGraceTicks - 1 : 0;
    }
}
