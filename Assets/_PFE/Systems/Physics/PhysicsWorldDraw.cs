using UnityEngine.LowLevelPhysics2D;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// The one place the LowLevelPhysics2D world's built-in debug draw is switched on or off.
    ///
    /// <para><b>Why this type exists: the "connected grey circles" are the ENGINE's draw, not ours.</b>
    /// <see cref="PhysicsWorld"/> debug-draws every world by default —
    /// <c>PhysicsWorldDefinition.defaultDefinition.drawOptions</c> is not
    /// <see cref="PhysicsWorld.DrawOptions.Off"/>, and <c>PhysicsWorldService</c> overrides only
    /// <c>simulateType</c>, <c>simulationWorkers</c>, <c>continuousAllowed</c> and <c>gravity</c>. So
    /// the chain mirror is rendered by Unity itself: the chain edges as lines and the chain vertices as
    /// dots, one tile apart, over every mirrored room. It appears in the <b>Editor and Development
    /// Player</b> only (ScriptReference, <c>PhysicsWorld.drawOptions</c>).</para>
    ///
    /// <para>It is invisible to <c>ColliderDebugOverlay</c> and to <c>PfeDebugSettings</c> by
    /// construction — it is a <c>PhysicsWorld</c> field, not one of our channels — which is exactly why
    /// <c>col off</c> could not silence it. It is also the reason the overlay's <c>physics</c> channel
    /// is worth having: that one draws the vertices we *emitted*, which the engine draw cannot
    /// distinguish from the rest of the world's geometry.</para>
    /// </summary>
    public static class PhysicsWorldDraw
    {
        /// <summary>
        /// The draw options for a given diagnostic state. <c>false</c> is the shipping default: the
        /// game and nothing else.
        /// </summary>
        public static PhysicsWorld.DrawOptions For(bool showDiagnostics)
        {
            return showDiagnostics ? Enabled : PhysicsWorld.DrawOptions.Off;
        }

        /// <summary>
        /// What "on" means, derived rather than guessed: the engine's own default, so enabling this
        /// reproduces exactly the draw the project shipped before the option existed.
        ///
        /// <para><b>The <c>Off</c> fallback is deliberate and is not a guess.</b> If a future Unity
        /// version ever ships <c>defaultDefinition.drawOptions == Off</c>, deriving alone would make
        /// this toggle a silent no-op — the "toggle is on and the screen shows nothing" failure this
        /// project has already hit once. <see cref="PhysicsWorld.DrawOptions.AllShapes"/> is then the
        /// explicit intent: draw the world's shapes, which is what the chain mirror consists of.</para>
        /// </summary>
        public static PhysicsWorld.DrawOptions Enabled
        {
            get
            {
                PhysicsWorld.DrawOptions engineDefault = PhysicsWorldDefinition.defaultDefinition.drawOptions;
                return engineDefault == PhysicsWorld.DrawOptions.Off
                    ? PhysicsWorld.DrawOptions.AllShapes
                    : engineDefault;
            }
        }
    }
}
