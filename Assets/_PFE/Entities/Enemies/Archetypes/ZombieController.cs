using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// Controller for Zombie / Ghoul enemy units (AS3 <c>fe/unit/UnitZombie.as</c>).
    ///
    /// <para><b>It deliberately declares no <c>Faction</c>.</b> The port's first version overrode it to
    /// <see cref="FactionType.Zombie"/>, which reads as the natural choice — there is a
    /// <c>F_ZOMBIE</c> constant, and this is the zombie controller. Both halves of that are wrong.
    /// AS3's <c>F_ZOMBIE</c> is <b>3</b> and is <i>declared and never assigned anywhere in the
    /// codebase</i>; the zombie family row is <c>&lt;unit id='zombie' fraction='1'&gt;</c>
    /// (<c>AllData.as:768</c>) — <b>1</b>, <c>F_MONSTER</c> — and <c>UnitZombie.resurrect()</c> writes
    /// <c>fraction = Unit.F_MONSTER</c> (<c>:506</c>) even for a zombie that has already been killed
    /// once. So the data and the oracle agree on Monster, and the override made the port disagree with
    /// both.</para>
    ///
    /// <para>It also happened to be harmless, which is exactly why it is worth deleting rather than
    /// leaving: <c>UnitController.Faction</c> already answers
    /// <c>_stats.fraction</c>, and <c>zombie0..zombie9</c> all inherit <c>fraction: 1</c> from the
    /// family, so the override returned a value nothing else produced and nothing yet reads. The first
    /// consumer that trusts <c>Faction</c> — friendly fire, a targeting filter, a faction-keyed
    /// dialogue — would have found every zombie in a faction of its own.</para>
    /// </summary>
    [RequireComponent(typeof(ZombieBrain))]
    public sealed class ZombieController : EnemyController
    {
        public const string ControllerId = "UnitZombie";
        public const string ControllerAlias = "zombie";

        protected override void Awake()
        {
            base.Awake();
            if (_brain == null)
            {
                _brain = GetComponent<ZombieBrain>();
            }
        }
    }
}
