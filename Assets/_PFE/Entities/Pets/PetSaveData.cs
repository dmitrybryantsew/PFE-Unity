using System;
using PFE.Systems.Pets;

namespace PFE.Entities.Pets
{
    /// <summary>
    /// The saved half of the companion state — AS3 <c>gg.currentPet</c>/<c>gg.noPet</c>/
    /// <c>gg.retPet</c> plus <c>Pers.owlhpProc</c> (<c>Pers.as:617-620</c>, <c>:753-755</c>,
    /// <c>:2369-2381</c>).
    ///
    /// <para><b>Why the HP is saved as a fraction and not a number.</b> The pet's maximum is a function
    /// of the player's level and their own <c>petHP</c>/<c>owlHP</c> stats
    /// (<see cref="PetRules.ScaleStats"/>), so a saved absolute HP would be wrong the moment the player
    /// levelled up between saving and loading. The oracle stores exactly the fraction
    /// (<c>owlhpProc = owl.hp / owl.maxhp</c>) and rebuilds at <c>maxhp * owlhpProc</c>, which is what
    /// this mirrors.</para>
    ///
    /// <para>A plain serialisable class with public fields, because that is what the save payload
    /// (<c>PlayerStateSnapshot</c>) holds for every other subsystem.</para>
    /// </summary>
    [Serializable]
    public class PetSaveData
    {
        /// <summary>AS3 <c>gg.currentPet.id</c>, or empty for "no pet out".</summary>
        public string currentPet = string.Empty;

        /// <summary>AS3 <c>gg.retPet</c> — the pet a potion should restore.</summary>
        public string returnPet = string.Empty;

        /// <summary>AS3 <c>gg.noPet</c> in ticks, so a cooldown survives a save.</summary>
        public int noPetTicks;

        /// <summary>The phoenix's HP as a fraction of its maximum.</summary>
        public float petHpFraction = 1f;

        /// <summary>The owl's — <c>Pers.owlhpProc</c>. <c>&lt;= 0</c> means dead.</summary>
        public float owlHpFraction = 1f;

        /// <summary>The moon's.</summary>
        public float moonHpFraction = 1f;

        /// <summary>Read one kind's fraction.</summary>
        public float GetHpFraction(PetKind kind)
        {
            switch (kind)
            {
                case PetKind.Owl: return owlHpFraction;
                case PetKind.Moon: return moonHpFraction;
                default: return petHpFraction;
            }
        }

        /// <summary>Write one kind's fraction.</summary>
        public void SetHpFraction(PetKind kind, float value)
        {
            switch (kind)
            {
                case PetKind.Owl: owlHpFraction = value; break;
                case PetKind.Moon: moonHpFraction = value; break;
                default: petHpFraction = value; break;
            }
        }
    }
}
