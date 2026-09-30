namespace PFE.Data.Definitions
{
    /// <summary>
    /// An armour item's per-damage-type resistance, AS3 <c>Armor.resist</c>.
    ///
    /// <para><b>Thirteen of these fields are parsed from XML, and exactly those thirteen.</b>
    /// <c>Armor.getXmlParam()</c> reads <c>bul</c>, <c>phis</c>, <c>blade</c>, <c>expl</c>, <c>fang</c>,
    /// <c>fire</c>, <c>cryo</c>, <c>laser</c>, <c>plasma</c>, <c>spark</c>, <c>acid</c>, <c>necro</c>,
    /// <c>venom</c> (<c>Armor.as:208-268</c>) and nothing else. <c>amul_adept</c> in <c>AllData</c>
    /// carries <c>dark='0.2'</c>, which AS3 <b>silently ignores</b> — so an importer that "helpfully"
    /// captured every attribute would produce a value the oracle does not honour. That is why
    /// <see cref="SetResistByAs3Attribute"/> is the only way in from XML, and it rejects <c>dark</c>.</para>
    ///
    /// <para><b>The fourteenth field, <see cref="pink"/>, is not data — it is a constructor rule.</b>
    /// <c>Armor.as:180-183</c> zeroes the whole array and then, if <c>tip == 1</c>, assigns
    /// <c>resist[D_PINK] = -0.5</c> with no XML attribute involved. It is modelled here anyway
    /// because <c>ArmourWear.ItemIntegrityDamage</c> already documents that it expects the value:
    /// AS3 applies <c>param1 *= 1 - resist[type]</c> <i>before</i> pink's own <c>×3</c>, so a body
    /// armour takes <b>×4.5</b> pink wear and an amulet <c>tip='3'</c> takes <c>×3</c>. Without a
    /// field for it the rule could not be expressed and the port quietly took <c>×3</c> for both.
    /// It is assigned by <c>ArmourState.FromItem</c> when the item is body armour, mirroring the
    /// AS3 constructor rather than the XML parse.</para>
    ///
    /// <para><b>Neutral is 0, not 1.</b> This is why the type is not <see cref="VulnerabilityData"/>,
    /// which looks like the same shape: that type is a damage <i>multiplier</i> table whose neutral is
    /// <c>1</c>, and it carries <c>poison</c>/<c>bleed</c>/<c>emp</c>/<c>pink</c> as well. Handing an
    /// armour's resistances to a vulnerability consumer would read those four unset fields as
    /// <c>0</c> — i.e. <b>immunity to poison, bleed, EMP and pink</b>. Sharing the type would make
    /// that mistake a one-word change.</para>
    ///
    /// <para><b>Two consumers, and the port has only one of them.</b> AS3 uses <c>resist</c> twice, for
    /// two different things:</para>
    /// <list type="number">
    /// <item><description><b>Armour wear</b> — <c>Armor.damage():321</c> does
    /// <c>param1 *= 1 - resist[type]</c>, so a bullet-resistant plate is also a slow-wearing one.
    /// Ported: <c>ArmourWear.ItemIntegrityDamage(type, damage, resist)</c>.</description></item>
    /// <item><description><b>The wearer's incoming damage</b> — <c>Pers.armorParameters():2067</c> does
    /// <c>gg.vulner[type] *= 1 - resist[type]</c>, and <c>Unit.damage():3529</c> then multiplies every
    /// hit by <c>vulner[type]</c>. <b>Not ported</b> — the port has no vulnerability term in its damage
    /// path at all. See the design doc's open question A7.</description></item>
    /// </list>
    ///
    /// <para><b>A correction to an earlier version of this note.</b> It said the term was missing "even
    /// though <c>UnitDefinition.vulnerabilities</c> is imported". The second half was <b>false</b>: the
    /// importer's regex tested <c>&lt;vuln\s+</c> against content that writes <c>&lt;vulner </c>, so it
    /// never matched and every unit carried the all-neutral default. The element is now parsed by
    /// <see cref="UnitVulnerabilityParser"/>, which is unit-tested. The term itself is still unported,
    /// so the data remains inert — but it is now real data rather than a default.</para>
    /// </summary>
    [System.Serializable]
    public struct ResistTable
    {
        // Physical
        public float bullet;      // AS3 @bul   -> DamageType.PhysicalBullet
        public float physical;    // AS3 @phis  -> DamageType.PhysicalMelee
        public float blade;       // AS3 @blade -> DamageType.Blade

        // Elemental / energy
        public float explosive;   // AS3 @expl   -> DamageType.Explosive
        public float fire;        // AS3 @fire   -> DamageType.Fire
        public float cryo;        // AS3 @cryo   -> DamageType.Cryo
        public float laser;       // AS3 @laser  -> DamageType.Laser
        public float plasma;      // AS3 @plasma -> DamageType.Plasma
        public float spark;       // AS3 @spark  -> DamageType.Spark

        // Biological / special
        public float fang;        // AS3 @fang   -> DamageType.Fang
        public float acid;        // AS3 @acid   -> DamageType.Acid
        public float necrotic;    // AS3 @necro  -> DamageType.Necrotic
        public float venom;       // AS3 @venom  -> DamageType.Venom

        /// <summary>
        /// Pink resistance — <b>never parsed from XML</b>. AS3 assigns <c>-0.5</c> in the constructor
        /// for every <c>tip == 1</c> (body) armour (<c>Armor.as:180-183</c>); see the type remarks.
        /// </summary>
        public float pink;        // AS3 resist[D_PINK], constructor-assigned -> DamageType.Pink

        /// <summary>
        /// Resistance for a damage type, or <c>0</c> — "no resistance" — for the seven types AS3's
        /// armour <c>resist</c> array leaves at zero (<c>poison</c>, <c>bleed</c>, <c>emp</c>,
        /// <c>balefire</c>, <c>psionic</c>, <c>astral</c>, <c>internal</c>).
        ///
        /// <para>Negative values are real and mean a <b>vulnerability</b>: <c>metal</c> armour has
        /// <c>spark='-0.3'</c>, and every <c>tip == 1</c> body armour is given
        /// <c>resist[D_PINK] = -0.5</c> by the constructor rather than by data
        /// (<c>Armor.as:180-183</c>).</para>
        /// </summary>
        public float GetResist(DamageType damageType)
        {
            switch (damageType)
            {
                case DamageType.PhysicalBullet:  return bullet;
                case DamageType.PhysicalMelee:   return physical;
                case DamageType.Blade:           return blade;
                case DamageType.Explosive:       return explosive;
                case DamageType.Fire:            return fire;
                case DamageType.Cryo:            return cryo;
                case DamageType.Laser:           return laser;
                case DamageType.Plasma:          return plasma;
                case DamageType.Spark:           return spark;
                case DamageType.Fang:            return fang;
                case DamageType.Acid:            return acid;
                case DamageType.Necrotic:        return necrotic;
                case DamageType.Venom:           return venom;
                case DamageType.Pink:            return pink;
                default:                         return 0f;
            }
        }

        /// <summary>Set resistance for a damage type. No-op for types AS3 cannot express.</summary>
        public void SetResist(DamageType damageType, float value)
        {
            switch (damageType)
            {
                case DamageType.PhysicalBullet:  bullet    = value; break;
                case DamageType.PhysicalMelee:   physical  = value; break;
                case DamageType.Blade:           blade     = value; break;
                case DamageType.Explosive:       explosive = value; break;
                case DamageType.Fire:            fire      = value; break;
                case DamageType.Cryo:            cryo      = value; break;
                case DamageType.Laser:           laser     = value; break;
                case DamageType.Plasma:          plasma    = value; break;
                case DamageType.Spark:           spark     = value; break;
                case DamageType.Fang:            fang      = value; break;
                case DamageType.Acid:            acid      = value; break;
                case DamageType.Necrotic:        necrotic  = value; break;
                case DamageType.Venom:           venom     = value; break;
                case DamageType.Pink:            pink      = value; break;
            }
        }

        /// <summary>
        /// Set resistance by AS3's own attribute name (<c>"bul"</c>, <c>"phis"</c>, <c>"necro"</c>, …).
        /// The importer uses this so the XML attribute names live in one place instead of in a switch
        /// at the call site.
        /// </summary>
        /// <returns><c>false</c> for an attribute AS3 does not parse, e.g. <c>dark</c>.</returns>
        public bool SetResistByAs3Attribute(string attributeName, float value)
        {
            switch (attributeName)
            {
                case "bul":   bullet    = value; return true;
                case "phis":  physical  = value; return true;
                case "blade": blade     = value; return true;
                case "expl":  explosive = value; return true;
                case "fire":  fire      = value; return true;
                case "cryo":  cryo      = value; return true;
                case "laser": laser     = value; return true;
                case "plasma": plasma   = value; return true;
                case "spark": spark     = value; return true;
                case "fang":  fang      = value; return true;
                case "acid":  acid      = value; return true;
                case "necro": necrotic  = value; return true;
                case "venom": venom     = value; return true;
                default:      return false;
            }
        }

        /// <summary>
        /// AS3's attribute names, in the order <c>Armor.getXmlParam()</c> reads them. Exposed so the
        /// importer and its tests agree on one list rather than two that can drift.
        /// </summary>
        public static readonly string[] As3AttributeNames =
        {
            "bul", "phis", "blade", "expl", "fang", "fire",
            "cryo", "laser", "plasma", "spark", "acid", "necro", "venom",
        };
    }
}
