using System;
using System.Globalization;

namespace PFE.Systems.Campaign
{
    /// <summary>
    /// One entry of the story-trigger table — AS3 <c>World.w.game.triggers[name]</c>
    /// (<c>Game.as:25</c>, written by <c>setTrigger</c> at <c>Game.as:676-679</c>).
    ///
    /// <para><b>Why this type exists.</b> The oracle's table is an untyped AS3 <c>Array</c> used as a
    /// map, and it holds <b>both ints and strings</b>: <c>"wait"</c> is written by <c>NPC.as:591</c> and
    /// consumed by <c>Game.as:516-522</c>, and a quest's <c>@set</c> value is written through verbatim
    /// (<c>Quest.as:332</c> <c>.toString()</c>, or the int <c>1</c> at <c>:336</c> when <c>@set</c> is
    /// absent). The port typed the table <c>Dictionary&lt;string,int&gt;</c>
    /// (<c>CampaignManager.cs:123</c>), which <b>cannot store a non-numeric string at all</b> — so a
    /// <c>"wait"</c> was coerced or dropped on write, and the refill sweep could never fire.</para>
    ///
    /// <para><b>What this type is not.</b> It is <i>not</i> a "preserve two disagreeing comparisons"
    /// fix. An earlier edition of the research claimed <c>PipPage.as:1530</c>'s <c>!= 1</c> (int) and
    /// <c>Location.as:1141</c>'s <c>== "1"</c> (string) disagree about a string <c>"1"</c>. They do not:
    /// AS3 <c>==</c>/<c>!=</c> are abstract equality and <b>coerce</b> a <c>String</c> operand to
    /// <c>Number</c>, so <c>"1" != 1</c> is <c>false</c> and both gates agree on <c>1</c>, on
    /// <c>"1"</c>, and on <c>"wait"</c> (which coerces to <c>NaN</c>). The oracle contains no strict
    /// <c>===</c> anywhere. So the read helpers below implement the <b>coercing</b> comparison, which is
    /// what the oracle actually does.</para>
    ///
    /// <para><b>NaN is modelled, not faked.</b> <c>"wait"</c> has no numeric value; AS3 gives it
    /// <c>NaN</c>, and every relational test against <c>NaN</c> is <c>false</c>. <see cref="TryAsNumber"/>
    /// returns <c>false</c> for it, so <see cref="IsGreaterThanZero"/> and <see cref="EqualsInt"/> are
    /// both <c>false</c> — matching <c>NaN &gt; 0</c> and <c>NaN == 1</c> — instead of pretending the
    /// value is <c>0</c>, which would make <c>&gt; 0</c> false but <c>&lt;= 0</c> true.</para>
    /// </summary>
    public readonly struct TriggerValue : IEquatable<TriggerValue>
    {
        /// <summary>Which arm of the union carries the value.</summary>
        public enum TriggerKind : byte
        {
            /// <summary>Not present in the table. AS3 <c>triggers[name] == undefined</c>.</summary>
            Unset = 0,

            /// <summary>A number. The overwhelming majority of the table.</summary>
            Int = 1,

            /// <summary>A string — <c>"wait"</c>, or a quest <c>@set</c> value.</summary>
            String = 2,
        }

        private readonly int _int;
        private readonly string _string;

        /// <summary>Which arm is live. A <c>default(TriggerValue)</c> is <see cref="TriggerKind.Unset"/>.</summary>
        public TriggerKind Kind { get; }

        /// <summary>Whether the key is present at all. AS3 <c>triggers[name] != null</c> — a question
        /// distinct from <c>&gt; 0</c>, and the oracle asks both (<c>Land.as:821</c> tests
        /// <c>== null</c>; <c>Area.as:360</c> tests <c>!= 1</c>).</summary>
        public bool IsSet => Kind != TriggerKind.Unset;

        /// <summary>The raw int payload. Meaningful only when <see cref="Kind"/> is
        /// <see cref="TriggerKind.Int"/>; otherwise 0.</summary>
        public int RawInt => _int;

        /// <summary>The raw string payload. Meaningful only when <see cref="Kind"/> is
        /// <see cref="TriggerKind.String"/>; otherwise <c>null</c>.</summary>
        public string RawString => _string;

        private TriggerValue(TriggerKind kind, int intPayload, string stringPayload)
        {
            Kind = kind;
            _int = intPayload;
            _string = stringPayload;
        }

        /// <summary>An absent entry. Identical to <c>default(TriggerValue)</c>.</summary>
        public static readonly TriggerValue Unset = default;

        /// <summary>A numeric entry.</summary>
        public static TriggerValue FromInt(int value) => new TriggerValue(TriggerKind.Int, value, null);

        /// <summary>A string entry. A <c>null</c> string reads as <see cref="Unset"/>, because the oracle
        /// never stores <c>null</c> in the table — it either has a value or the key is absent.</summary>
        public static TriggerValue FromString(string value) =>
            value == null ? Unset : new TriggerValue(TriggerKind.String, 0, value);

        /// <summary>
        /// The value an authored <c>&lt;s act="trigger"&gt;</c> writes — AS3
        /// <c>Script.com</c>'s <c>trigger</c> branch, <c>World.w.game.setTrigger(param1.val, param1.n)</c>
        /// (<c>Script.as:424-434</c>), where <c>param1.n</c> was built by <c>analiz</c>
        /// (<c>Script.as:71</c>, <c>:97-99</c>).
        ///
        /// <para><b>The result is always a <i>string</i>, and that is not obvious.</b> <c>analiz</c>
        /// declares <c>var _loc6_:String = "-1"</c> and only overwrites it when <c>@n</c> is present and
        /// non-empty (<c>if(param1.@n.length())</c>). So <c>n</c> is <b>never null</b>, which makes
        /// <c>com</c>'s <c>if(param1.n != null)</c> guard unconditionally true and the
        /// <c>else { setTrigger(param1.val) }</c> arm (<c>:432</c>, the int <c>1</c>) <b>dead code</b>.
        /// A bare <c>&lt;s act="trigger" val="x"/&gt;</c> therefore stores the <b>string</b>
        /// <c>"-1"</c> — not the int <c>1</c>.</para>
        ///
        /// <para>This matters for exactly one input: a non-numeric <c>@n</c>. The old port mapped
        /// <c>n</c> through <c>int.TryParse</c> and substituted <c>1</c> on failure
        /// (<c>AreaTriggerSystem.cs:320</c>), so <c>@n="wait"</c> became the int <c>1</c> while the oracle
        /// stores <c>"wait"</c> — and <c>"wait"</c> is the value <c>refill</c> sweeps for
        /// (<c>Game.as:516-522</c>). For a numeric <c>@n</c>, or for the <c>"-1"</c> default, the string
        /// and int forms are indistinguishable through every reader (AS3 <c>==</c> coerces), so this
        /// change is behaviour-preserving everywhere except that one case.</para>
        /// </summary>
        /// <param name="n">The authored <c>@n</c> attribute, or null/empty when absent.</param>
        public static TriggerValue FromScriptCommandN(string n) =>
            FromString(string.IsNullOrEmpty(n) ? "-1" : n);

        /// <summary>
        /// AS3 <c>Number(value)</c>, including its <c>NaN</c> case. Returns <c>false</c> when the value has
        /// no numeric meaning (an absent key, or a non-numeric string) — the caller decides what that means,
        /// which is the whole reason this returns a bool rather than a sentinel.
        /// </summary>
        public bool TryAsNumber(out double number)
        {
            switch (Kind)
            {
                case TriggerKind.Int:
                    number = _int;
                    return true;

                case TriggerKind.String:
                    // AS3 Number("1") == 1, Number(" 2 ") == 2, Number("wait") == NaN.
                    // InvariantCulture: the save file and the authored data are both invariant.
                    return double.TryParse(_string, NumberStyles.Float, CultureInfo.InvariantCulture,
                                           out number);

                default:
                    number = 0d;
                    return false;
            }
        }

        /// <summary>
        /// The numeric value, or <paramref name="defaultIfNotNumber"/> when the entry is absent or the
        /// string is non-numeric.
        ///
        /// <para><b>The default is a deliberate deviation.</b> AS3 would yield <c>NaN</c>, and
        /// <c>NaN + 1</c> is <c>NaN</c>. An <c>int</c> cannot be <c>NaN</c>, so the one caller that does
        /// arithmetic — the <c>prob_&lt;id&gt;</c> counter at <c>ProbDoorContext.cs:96</c> — must choose.
        /// It chooses <c>0</c>, which makes <c>"wait" + 1</c> read as <c>1</c> rather than as an
        /// indeterminate value; that counter is only ever written numerically, so the case is
        /// unreachable in practice and the choice is documented rather than hidden.</para>
        /// </summary>
        public int AsInt(int defaultIfNotNumber) =>
            TryAsNumber(out double number) ? (int)number : defaultIfNotNumber;

        /// <summary>AS3 <c>String(value)</c>: the string itself, or the invariant spelling of the int.
        /// An absent entry yields <c>null</c>.</summary>
        public string AsString()
        {
            switch (Kind)
            {
                case TriggerKind.String: return _string;
                case TriggerKind.Int: return _int.ToString(CultureInfo.InvariantCulture);
                default: return null;
            }
        }

        /// <summary>
        /// AS3 <c>value == n</c> — <b>with</b> the string→number coercion the oracle relies on. A string
        /// <c>"1"</c> therefore equals the int <c>1</c> (<c>PipPage.as:1530</c>), and a non-numeric
        /// string equals nothing (<c>NaN == n</c> is <c>false</c>).
        /// </summary>
        public bool EqualsInt(int n) => TryAsNumber(out double number) && number == n;

        /// <summary>
        /// AS3 <c>value == s</c>. A string entry is compared ordinally (AS3 compares two <c>String</c>s as
        /// strings); an int entry coerces the operand to a number, matching <c>1 == "1"</c>.
        ///
        /// <para>This is the refill sweep's test — <c>Game.as:516-522</c> asks <c>== "wait"</c>, and a
        /// value stored as the int <c>1</c> must answer <c>false</c>, which it does because
        /// <c>"wait"</c> does not parse as a number.</para>
        /// </summary>
        public bool EqualsString(string s)
        {
            if (s == null) return false;

            if (Kind == TriggerKind.String)
            {
                return string.Equals(_string, s, StringComparison.Ordinal);
            }

            return TryAsNumber(out double left)
                && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double right)
                && left == right;
        }

        /// <summary>AS3 <c>value &gt; 0</c>. A non-numeric string answers <c>false</c>, exactly as
        /// <c>NaN &gt; 0</c> does — this is the <c>prob_&lt;id&gt;</c> / <c>mbase_visited</c> gate.</summary>
        public bool IsGreaterThanZero() => TryAsNumber(out double number) && number > 0d;

        /// <summary>Value equality over (kind, payload).</summary>
        public bool Equals(TriggerValue other)
        {
            if (Kind != other.Kind) return false;
            switch (Kind)
            {
                case TriggerKind.Int: return _int == other._int;
                case TriggerKind.String: return string.Equals(_string, other._string, StringComparison.Ordinal);
                default: return true;
            }
        }

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is TriggerValue other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            switch (Kind)
            {
                case TriggerKind.Int: return (int)Kind * 397 ^ _int;
                case TriggerKind.String: return (int)Kind * 397 ^ (_string?.GetHashCode() ?? 0);
                default: return (int)Kind;
            }
        }

        public static bool operator ==(TriggerValue a, TriggerValue b) => a.Equals(b);
        public static bool operator !=(TriggerValue a, TriggerValue b) => !a.Equals(b);

        /// <summary>The payload as text — the tab and the console print this. An absent entry prints
        /// <c>(unset)</c> so a readout can never confuse "absent" with "0".</summary>
        public override string ToString()
        {
            switch (Kind)
            {
                case TriggerKind.String: return _string;
                case TriggerKind.Int: return _int.ToString(CultureInfo.InvariantCulture);
                default: return "(unset)";
            }
        }
    }
}
