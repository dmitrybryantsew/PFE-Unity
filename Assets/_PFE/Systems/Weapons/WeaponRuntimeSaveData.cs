using System;
using UnityEngine;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Serializable snapshot of an equipped weapon's runtime state.
    /// All timers are preserved in integer simulation ticks (30 Hz cadence matching AS3), never seconds.
    /// </summary>
    [Serializable]
    public class WeaponRuntimeSaveData
    {
        public string weaponId;
        public int currentAmmo;
        public int currentDurability;
        public bool jammed;
        public int kolShoot;

        // Frame-counter timers (30 fps cadence, matching AS3) - strictly integer simulation ticks!
        public int tAttack;
        public int tReload;
        public int tPrep;
        public int tRet;
        public int tRech;
        public int tRel;
        public int tShoot;
        public int tAuto;
        public int pow;
        public float rotUp;

        public static WeaponRuntimeSaveData FromRuntimeState(WeaponRuntimeState state)
        {
            if (state == null) return null;

            return new WeaponRuntimeSaveData
            {
                weaponId = state.Def != null ? state.Def.weaponId : "",
                currentAmmo = state.CurrentAmmo,
                currentDurability = state.CurrentDurability,
                jammed = state.Jammed,
                kolShoot = state.KolShoot,
                tAttack = state.TAttack,
                tReload = state.TReload,
                tPrep = state.TPrep,
                tRet = state.TRet,
                tRech = state.TRech,
                tRel = state.TRel,
                tShoot = state.TShoot,
                tAuto = state.TAuto,
                pow = state.Pow,
                rotUp = state.RotUp
            };
        }

        public void RestoreTo(WeaponRuntimeState state)
        {
            if (state == null) return;

            state.CurrentAmmo = currentAmmo;
            state.CurrentDurability = currentDurability;
            state.Jammed = jammed;
            state.KolShoot = kolShoot;
            state.TAttack = tAttack;
            state.TReload = tReload;
            state.TPrep = tPrep;
            state.TRet = tRet;
            state.TRech = tRech;
            state.TRel = tRel;
            state.TShoot = tShoot;
            state.TAuto = tAuto;
            state.Pow = pow;
            state.RotUp = rotUp;
            state.SyncReactive();
        }
    }
}
