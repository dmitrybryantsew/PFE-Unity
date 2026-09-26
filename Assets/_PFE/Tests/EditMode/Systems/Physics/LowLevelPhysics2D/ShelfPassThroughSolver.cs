using UnityEngine;
using UnityEngine.LowLevelPhysics2D;
using PFE.Systems.Map.TileQuery;

namespace PFE.Tests.EditMode.Systems.Physics.LowLevelPhysics2D
{
    /// <summary>
    /// Hand-written one-way ("shelf") pass-through, installed as a pre-solve callback.
    ///
    /// <para>Stage A throwaway. Answers guide §5 question 4 and feeds open decision L3.</para>
    ///
    /// <para><b>The rule is AS3's, not Box2D's.</b> The API has no effector equivalent; the docs say
    /// a pre-solve callback is <i>"a typical use-case ... to implement a one-way behaviour based upon
    /// the provided contact"</i>. The behaviour implemented here is
    /// <c>Unit.as:2578</c> (<c>collisionTile</c>), whose shelf clause reads:</para>
    ///
    /// <code>
    /// if ((phis == 0 || (transT &amp;&amp; phis == 3)) &amp;&amp; shelf
    ///     &amp;&amp; (Y2 - (stay ? porog : porog_jump) &gt; phY1
    ///         || throu || t_throw &gt; 0 || levit || isFly || diagon != 0))
    /// {
    ///    return 0;   // no collision -> pass through
    /// }
    /// </code>
    ///
    /// <para>Read in AS3 space (Y grows downward): <c>Y2</c> is the unit's bottom, <c>phY1</c> the
    /// tile's top edge. Pass through when the bottom, less the step-up allowance <c>porog</c>, is
    /// still <i>below</i> the platform's top — i.e. the unit has not yet risen onto the surface —
    /// or when an explicit drop-through flag is set (<c>throu</c> / <c>t_throw</c>).</para>
    ///
    /// <para>In a Unity world +Y is up, so the same test is
    /// <c>bodyBottom &lt; shelfTop - porog</c>.</para>
    ///
    /// <para><b>Why this is a ScriptableObject.</b> The API requires a callback target that is a
    /// <c>UnityEngine.Object</c>: <i>"An interface that when implemented by a UnityEngine.Object
    /// ..."</i>. A plain C# class can be collected out from under the shape.</para>
    /// </summary>
    public sealed class ShelfPassThroughSolver : ScriptableObject, PhysicsCallbacks.IPreSolveCallback
    {
        /// <summary>
        /// What the callback should decide.
        ///
        /// <para><see cref="Mode.ForceTrue"/> and <see cref="Mode.ForceFalse"/> exist to calibrate
        /// the API's return convention at runtime: the shipped docs say <i>"This allows a contact to
        /// be disabled before it goes to the solver"</i> but never state which return value disables.
        /// Rather than hard-code a guess, the spike runs both and infers it.</para>
        /// </summary>
        public enum Mode
        {
            /// <summary>Apply the AS3 <c>Unit.as:2578</c> rule.</summary>
            As3Rule = 0,

            /// <summary>Return true unconditionally — calibration probe.</summary>
            ForceTrue = 1,

            /// <summary>Return false unconditionally — calibration probe.</summary>
            ForceFalse = 2,
        }

        public Mode SolverMode = Mode.As3Rule;

        /// <summary>
        /// Which formulation of "should this contact hold" to apply. Both are measured by the spike;
        /// see the write-up for why they differ.
        /// </summary>
        public enum Rule
        {
            /// <summary>
            /// AS3's literal test, <c>Unit.as:2578</c>: hold when the bottom is within <c>porog</c>
            /// of the surface.
            /// </summary>
            As3Position = 0,

            /// <summary>
            /// Hold when the body is descending onto the surface (or stationary on it). Not AS3's
            /// formulation, but the one a solver can actually evaluate.
            /// </summary>
            ApproachDirection = 1,
        }

        public Rule RuleMode = Rule.ApproachDirection;

        /// <summary>
        /// The return value that DISABLES the contact. Set from the calibration probe, so the rule
        /// is expressed in terms of intent ("block" / "pass") instead of a magic boolean.
        /// </summary>
        public bool DisableReturnValue = false;
        /// <summary>Shelf surface height, Unity units, +Y up. AS3 <c>phY1</c>.</summary>
        public float ShelfTopY;

        /// <summary>Body half-height, Unity units. Used to derive AS3 <c>Y2</c> from the centre.</summary>
        public float BodyHalfHeight;

        /// <summary>Step-up allowance in Unity units. AS3 <c>Unit.as:278</c> (<c>porog = 10</c> px).</summary>
        public float PorogUnits = TileQueryConstants.PorogGrounded * Llp2d.PixelToUnit;

        /// <summary>
        /// AS3 <c>throu</c> / <c>t_throw &gt; 0</c>: the player is explicitly dropping through.
        /// Overrides the positional test entirely.
        /// </summary>
        public bool DropThroughRequested;

        /// <summary>How many times the solver was consulted. Zero means the callback never fired.</summary>
        public int InvocationCount;

        /// <summary>
        /// Called during the world step, possibly from a worker thread. Only safe reads are allowed
        /// here — no writes to the world. See the API docs on <c>IPreSolveCallback</c>.
        /// </summary>
        /// <returns>True to keep the contact, false to disable it before it reaches the solver.</returns>
        public bool OnPreSolve2D(PhysicsEvents.PreSolveEvent e)
        {
            InvocationCount++;

            switch (SolverMode)
            {
                case Mode.ForceTrue:
                    return true;
                case Mode.ForceFalse:
                    return false;
            }

            bool block = !DropThroughRequested && ShouldBlock(e);

            // Expressed via the calibrated constant so the rule reads as intent.
            return block ? !DisableReturnValue : DisableReturnValue;
        }

        private bool ShouldBlock(PhysicsEvents.PreSolveEvent e)
        {
            PhysicsBody body = PickDynamicBody(e.shapeA, e.shapeB);
            if (!body.isValid)
            {
                return true;
            }

            if (RuleMode == Rule.ApproachDirection)
            {
                // Descending onto the surface, or already resting on it. Rising past it does not
                // collide, which is the one-way behaviour players expect.
                return body.linearVelocity.y <= 0f;
            }

            // AS3: Y2 - porog > phY1 means no collision. Y-flipped for a +Y-up world, so a contact
            // is held when the body's bottom has risen to within `porog` of the top.
            //
            // Measured caveat: the callback runs AFTER integration, so by the time this is
            // evaluated a falling body has already penetrated the surface and reads as "below it",
            // which flips the answer and drops the body through. See the Stage A write-up.
            return body.position.y - BodyHalfHeight >= ShelfTopY - PorogUnits;
        }

        private static PhysicsBody PickDynamicBody(PhysicsShape a, PhysicsShape b)
        {
            if (a.isValid && a.body.isValid && a.body.type == PhysicsBody.BodyType.Dynamic)
            {
                return a.body;
            }

            if (b.isValid && b.body.isValid && b.body.type == PhysicsBody.BodyType.Dynamic)
            {
                return b.body;
            }

            return default;
        }
    }
}
