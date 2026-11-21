namespace PFE.Systems.RPG
{
    /// <summary>
    /// Immutable snapshot of the movement/physics derived stats — the fields
    /// <see cref="CharacterStats"/> recomputes in <c>RecalculateStats</c> and the locomotion layer
    /// reads every tick (speed multipliers, jump, levitation, mana-regen economy, stealth-movement
    /// terms).
    ///
    /// <para><b>This is a copy, not a view.</b> Hot paths can hold the struct across a tick without
    /// aliasing the live fields; the next access through <see cref="CharacterStats.Locomotion"/>
    /// after a stat change returns the refreshed snapshot. Every member maps 1:1 to a documented
    /// field on <see cref="CharacterStats"/> — read there for the AS3 provenance.</para>
    /// </summary>
    public readonly struct LocomotionStatBlock
    {
        public readonly float AllSpeedMult;
        public readonly float RunSpeedMult;
        public readonly float StamRun;
        public readonly float StamRes;
        public readonly float MaxOd;
        public readonly float Jumpdy;
        public readonly float Djumpdy;
        public readonly float Tormoz;
        public readonly int AbleFly;
        public readonly float AlicornFlyMult;
        public readonly float MeleeRun;
        public readonly float MeleeR;
        public readonly int LevitOn;
        public readonly float LevitDMana;
        public readonly float LevitDManaUp;
        public readonly int RunForever;
        public readonly int Zaput;
        public readonly int AttackForever;
        public readonly int AutoAttack;
        public readonly float StealthMult;
        public readonly float NoiseRun;
        public readonly float Sneak;
        public readonly float SneakLurk;
        public readonly float RecMana;
        public readonly float RecManaMin;
        public readonly float ShtrManaRes;
        public readonly float ManaHpRes;

        internal LocomotionStatBlock(CharacterStats s)
        {
            AllSpeedMult  = s.allSpeedMult;
            RunSpeedMult  = s.runSpeedMult;
            StamRun       = s.stamRun;
            StamRes       = s.stamRes;
            MaxOd         = s.maxOd;
            Jumpdy        = s.jumpdy;
            Djumpdy       = s.djumpdy;
            Tormoz        = s.tormoz;
            AbleFly       = s.ableFly;
            AlicornFlyMult = s.alicornFlyMult;
            MeleeRun      = s.meleeRun;
            MeleeR        = s.meleeR;
            LevitOn       = s.levitOn;
            LevitDMana    = s.levitDMana;
            LevitDManaUp  = s.levitDManaUp;
            RunForever    = s.runForever;
            Zaput         = s.zaput;
            AttackForever = s.attackForever;
            AutoAttack    = s.autoAttack;
            StealthMult   = s.stealthMult;
            NoiseRun      = s.noiseRun;
            Sneak         = s.sneak;
            SneakLurk     = s.sneakLurk;
            RecMana       = s.recMana;
            RecManaMin    = s.recManaMin;
            ShtrManaRes   = s.shtrManaRes;
            ManaHpRes     = s.manaHPRes;
        }
    }

    /// <summary>
    /// Immutable snapshot of the combat derived stats — damage multipliers, crit, precision,
    /// per-monster bonuses, and the weapon-handling multipliers. Read by the damage calculator,
    /// weapon controllers, and S.A.T.S. per shot. Same copy-not-a-view contract as
    /// <see cref="LocomotionStatBlock"/>; AS3 provenance lives on the
    /// <see cref="CharacterStats"/> fields.
    /// </summary>
    public readonly struct CombatStatBlock
    {
        public readonly float AllDamMult;
        public readonly float AllVulnerMult;
        public readonly float CritCh;
        public readonly float CritDamMult;
        public readonly float CritInvis;
        public readonly float Desintegr;
        public readonly float Dexter;
        public readonly float DodgePlus;
        public readonly float Skin;
        public readonly float MeleeDamMult;
        public readonly float MeleeSpdMult;
        public readonly float GunsDamMult;
        public readonly float SpellsDamMult;
        public readonly float PunchDamMult;
        public readonly float AllPrecMult;
        public readonly float RunPenalty;
        public readonly float JumpPenalty;
        public readonly float BackPenalty;
        public readonly float StayBonus;
        public readonly float MazilAdd;
        public readonly float SatsMult;
        public readonly float DieDamage;
        public readonly float RadX;
        public readonly float ReloadMult;
        public readonly float RecoilMult;
        public readonly float JammedMult;
        public readonly float Recyc;
        public readonly float Knocked;
        public readonly float KickDestroy;
        public readonly float LastCh;
        public readonly float PipEmpVulner;
        public readonly float DamPony;
        public readonly float DamZombie;
        public readonly float DamRobot;
        public readonly float DamInsect;
        public readonly float DamMonster;
        public readonly float DamAlicorn;
        public readonly float ReanimHp;
        public readonly float RegenFew;
        public readonly float RegenMax;

        internal CombatStatBlock(CharacterStats s)
        {
            AllDamMult     = s.allDamMult;
            AllVulnerMult  = s.allVulnerMult;
            CritCh         = s.critCh;
            CritDamMult    = s.critDamMult;
            CritInvis      = s.critInvis;
            Desintegr      = s.desintegr;
            Dexter         = s.dexter;
            DodgePlus      = s.dodgePlus;
            Skin           = s.skin;
            MeleeDamMult   = s.meleeDamMult;
            MeleeSpdMult   = s.meleeSpdMult;
            GunsDamMult    = s.gunsDamMult;
            SpellsDamMult  = s.spellsDamMult;
            PunchDamMult   = s.punchDamMult;
            AllPrecMult    = s.allPrecMult;
            RunPenalty     = s.runPenalty;
            JumpPenalty    = s.jumpPenalty;
            BackPenalty    = s.backPenalty;
            StayBonus      = s.stayBonus;
            MazilAdd       = s.mazilAdd;
            SatsMult       = s.satsMult;
            DieDamage      = s.dieDamage;
            RadX           = s.radX;
            ReloadMult     = s.reloadMult;
            RecoilMult     = s.recoilMult;
            JammedMult     = s.jammedMult;
            Recyc          = s.recyc;
            Knocked        = s.knocked;
            KickDestroy    = s.kickDestroy;
            LastCh         = s.lastCh;
            PipEmpVulner   = s.pipEmpVulner;
            DamPony        = s.damPony;
            DamZombie      = s.damZombie;
            DamRobot       = s.damRobot;
            DamInsect      = s.damInsect;
            DamMonster     = s.damMonster;
            DamAlicorn     = s.damAlicorn;
            ReanimHp       = s.reanimHp;
            RegenFew       = s.regenFew;
            RegenMax       = s.regenMax;
        }
    }

    /// <summary>
    /// Immutable snapshot of the interaction/economy derived stats — lockpick, hacking, repair,
    /// crafting, barter, telekinesis tuning, spellcasting, companions, and inventory limits. Read at
    /// low frequency (world checks, workbench/PipBuck UI, pet spawning). Same copy-not-a-view
    /// contract as <see cref="LocomotionStatBlock"/>; AS3 provenance lives on the
    /// <see cref="CharacterStats"/> fields.
    /// </summary>
    public readonly struct UtilityStatBlock
    {
        public readonly float ModAnalis;
        public readonly float VisiTrap;
        public readonly float Medic;
        public readonly int LockPick;
        public readonly int PossLockPick;
        public readonly int LockPickTime;
        public readonly int UnlockMaster;
        public readonly float LockAtt;
        public readonly float PinBreak;
        public readonly float UpChance;
        public readonly int Hacker;
        public readonly int HackerMaster;
        public readonly int HackAtt;
        public readonly int Security;
        public readonly int Repair;
        public readonly float RepairMult;
        public readonly int MaxArmorLvl;
        public readonly int Potmaster;
        public readonly int Remine;
        public readonly int Signal;
        public readonly int BarterLvl;
        public readonly float LimitBuys;
        public readonly float CapsMult;
        public readonly float DifCapsMult;
        public readonly int HimLevel;
        public readonly int Eco;
        public readonly int Freel;
        public readonly int Infravis;
        public readonly int DropTre;
        public readonly float PotShad;
        public readonly int IsDJ;
        public readonly int Ad1;
        public readonly int Ad2;
        public readonly int Ad3;
        public readonly int Admax;

        // Telekinesis & magic economy (AS3 Pers.as telekinesis block)
        public readonly float MaxTeleMassa;
        public readonly float TeleDist;
        public readonly float TelePorog;
        public readonly float TeleMult;
        public readonly float ThrowForce;
        public readonly float ThrowDmagic;
        public readonly float ThrowDmanaMult;
        public readonly float ThrowDmana;
        public readonly float AllDManaMult;
        public readonly int Telemaster;
        public readonly float WarlockDManaMult;
        public readonly float UnitLevitMult;
        public readonly float TeleEnemy;
        public readonly float TeleSpeed;
        public readonly float TeleAccel;
        public readonly float TeleMana;
        public readonly float TeleManaMult;
        public readonly float AlicornRunMana;
        public readonly float PortMana;
        public readonly int PortPoss;
        public readonly float AlicornPortMana;
        public readonly float TelePower;
        public readonly float SpellPower;
        public readonly float SpellDown;
        public readonly float AlicornShitHp;
        public readonly float AlicornHeal;

        // Companions (AS3 Pers pet/owl/moon block)
        public readonly float PetHP;
        public readonly float PetDam;
        public readonly float PetRes;
        public readonly float PetSkin;
        public readonly float PetVulner;
        public readonly float OwlHP;
        public readonly float OwlDam;
        public readonly float OwlSkin;
        public readonly float OwlVulner;
        public readonly float MoonHP;
        public readonly float MoonDam;

        // Ammo/inventory limits (AS3 Pers maxm* block)
        public readonly float MaxmW;
        public readonly float MaxmM;
        public readonly float Maxm1;
        public readonly float Maxm2;
        public readonly float Maxm3;

        internal UtilityStatBlock(CharacterStats s)
        {
            ModAnalis       = s.modAnalis;
            VisiTrap        = s.visiTrap;
            Medic           = s.medic;
            LockPick        = s.lockPick;
            PossLockPick    = s.possLockPick;
            LockPickTime    = s.lockPickTime;
            UnlockMaster    = s.unlockMaster;
            LockAtt         = s.lockAtt;
            PinBreak        = s.pinBreak;
            UpChance        = s.upChance;
            Hacker          = s.hacker;
            HackerMaster    = s.hackerMaster;
            HackAtt         = s.hackAtt;
            Security        = s.security;
            Repair          = s.repair;
            RepairMult      = s.repairMult;
            MaxArmorLvl     = s.maxArmorLvl;
            Potmaster       = s.potmaster;
            Remine          = s.remine;
            Signal          = s.signal;
            BarterLvl       = s.barterLvl;
            LimitBuys       = s.limitBuys;
            CapsMult        = s.capsMult;
            DifCapsMult     = s.difCapsMult;
            HimLevel        = s.himLevel;
            Eco             = s.eco;
            Freel           = s.freel;
            Infravis        = s.infravis;
            DropTre         = s.dropTre;
            PotShad         = s.potShad;
            IsDJ            = s.isDJ;
            Ad1             = s.ad1;
            Ad2             = s.ad2;
            Ad3             = s.ad3;
            Admax           = s.admax;

            MaxTeleMassa    = s.maxTeleMassa;
            TeleDist        = s.teleDist;
            TelePorog       = s.telePorog;
            TeleMult        = s.teleMult;
            ThrowForce      = s.throwForce;
            ThrowDmagic     = s.throwDmagic;
            ThrowDmanaMult  = s.throwDmanaMult;
            ThrowDmana      = s.throwDmana;
            AllDManaMult    = s.allDManaMult;
            Telemaster      = s.telemaster;
            WarlockDManaMult = s.warlockDManaMult;
            UnitLevitMult   = s.unitLevitMult;
            TeleEnemy       = s.teleEnemy;
            TeleSpeed       = s.teleSpeed;
            TeleAccel       = s.teleAccel;
            TeleMana        = s.teleMana;
            TeleManaMult    = s.teleManaMult;
            AlicornRunMana  = s.alicornRunMana;
            PortMana        = s.portMana;
            PortPoss        = s.portPoss;
            AlicornPortMana = s.alicornPortMana;
            TelePower       = s.telePower;
            SpellPower      = s.spellPower;
            SpellDown       = s.spellDown;
            AlicornShitHp   = s.alicornShitHP;
            AlicornHeal     = s.alicornHeal;

            PetHP           = s.petHP;
            PetDam          = s.petDam;
            PetRes          = s.petRes;
            PetSkin         = s.petSkin;
            PetVulner       = s.petVulner;
            OwlHP           = s.owlHP;
            OwlDam          = s.owlDam;
            OwlSkin         = s.owlSkin;
            OwlVulner       = s.owlVulner;
            MoonHP          = s.moonHP;
            MoonDam         = s.moonDam;

            MaxmW           = s.maxmW;
            MaxmM           = s.maxmM;
            Maxm1           = s.maxm1;
            Maxm2           = s.maxm2;
            Maxm3           = s.maxm3;
        }
    }
}
