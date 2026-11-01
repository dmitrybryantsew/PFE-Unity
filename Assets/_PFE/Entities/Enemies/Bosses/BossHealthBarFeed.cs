using System;

namespace PFE.Entities.Enemies.Bosses
{
    /// <summary>
    /// The seam between a boss's health and the HUD's top-centre boss bar — the port of
    /// <c>GUI.hpBarBoss</c> (<c>GUI.as:1693-1705</c>) and its three call sites.
    ///
    /// <para><b>What the Oracle does.</b> A boss never draws its own floating health bar. Every frame
    /// <c>Unit.visDetails()</c> takes the boss arm (<c>Unit.as:2985-2991</c>): it calls
    /// <c>World.w.gui.hpBarBoss(hp / maxhp)</c> and sets <c>this.hpbar.visible = false</c>. The GUI
    /// method is three lines — <c>param1 &lt;= 0</c> hides the bar, otherwise it shows it and writes
    /// <c>vis.hpbarboss.bar.scaleX = param1</c>. Two more places hide it: <c>Unit.die()</c>
    /// (<c>:4308-4311</c>, only on the death that sticks) and <c>World.ativateLoc</c> (<c>:1088</c>, so a
    /// room change cannot inherit the previous room's bar).</para>
    ///
    /// <para><b>Why a static and not a MessagePipe broker.</b> The publisher is
    /// <c>BossController.TickControl</c> — a unit built with <c>AddComponent</c> by the room spawner, so
    /// nothing injects it and there is no <c>[Inject]</c> site for an <c>IPublisher&lt;T&gt;</c>. This is
    /// the same shape as <c>ItemPickupFeedback</c> and <c>EliteEnemyRegistry</c>, and for the same
    /// reason: the producer lives in the entity layer and must not know what a HUD is.</para>
    ///
    /// <para><b>Last reporter wins, exactly as in the Oracle.</b> Two live bosses each push their own
    /// fraction every step, so the bar shows whichever stepped last; when one dies it hides the bar and
    /// the other re-shows it on its next step. That is faithful rather than tidy — the Oracle has one
    /// boss per room and never had to decide — and it is why <see cref="Clear"/> is safe to call
    /// unconditionally.</para>
    ///
    /// <para><b>Zero subscribers is the normal case outside a boss room.</b> <see cref="Report"/> is
    /// called from a 30 Hz tick, so it checks the delegate before building anything; with no HUD in the
    /// scene (an EditMode test, a bare spawn harness) both calls are a null check.</para>
    /// </summary>
    public static class BossHealthBarFeed
    {
        /// <summary>
        /// A live boss's health as a fraction of its maximum, in <c>(0, 1]</c>. Raised once per sim
        /// step per live boss — the port's <c>visDetails()</c> cadence.
        /// </summary>
        public static event Action<float> Reported;

        /// <summary>
        /// The bar must go away: the boss died on the death that stuck, or it was destroyed without
        /// one. Raised by <see cref="BossController"/>'s <c>OnDeath</c> / <c>OnDestroy</c>.
        /// </summary>
        public static event Action Cleared;

        /// <summary>
        /// Publish one boss's health. A fraction of zero or less <b>hides</b> the bar rather than
        /// drawing an empty one, because that is what <c>GUI.hpBarBoss</c> does with
        /// <c>param1 &lt;= 0</c> (<c>GUI.as:1695-1697</c>).
        /// </summary>
        public static void Report(float fraction)
        {
            Action<float> reported = Reported;
            if (reported != null)
            {
                reported(fraction);
            }
        }

        /// <summary>Hide the bar. Idempotent, and harmless when nothing is showing.</summary>
        public static void Clear()
        {
            Action cleared = Cleared;
            if (cleared != null)
            {
                cleared();
            }
        }

        /// <summary>
        /// Drop every subscriber — for a test, or for a new session. The static counterpart of
        /// <c>EliteEnemyRegistry.Reset</c>: a view that was destroyed without unsubscribing would
        /// otherwise keep a delegate alive across play-mode runs in the editor.
        /// </summary>
        public static void Reset()
        {
            Reported = null;
            Cleared = null;
        }
    }
}
