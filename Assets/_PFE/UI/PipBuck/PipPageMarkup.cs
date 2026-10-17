namespace PFE.UI.PipBuck
{
    /// <summary>
    /// The four inline colour wrappers the PipBuck pages write into their text fields — the port of
    /// <c>PipPage.yel</c> / <c>red</c> / <c>blue</c> (<c>PipPage.as:192-216</c>) and of the two class
    /// names the page bodies write by hand.
    ///
    /// <para><b>Why the port emits Flash spans and not Unity colour tags.</b> A page body is a string
    /// the Oracle assembles from ~15 fragments, and the fragments' <i>order and nesting</i> are part of
    /// the behaviour — a wrong close tag changes which words are yellow. Keeping the model's output
    /// byte-identical to the Oracle's makes that assertable offline;
    /// <c>PFE.UI.Common.PfeTextFormatter</c> then resolves the classes to <c>&lt;color=#RRGGBB&gt;</c>
    /// at the single point where the string reaches a label. Doing the conversion here as well would
    /// give the project two formatters that can drift.</para>
    ///
    /// <para><b>The spacing is the Oracle's, and it is inconsistent.</b> <c>yel</c> writes
    /// <c>&lt;span class = 'yel'&gt;</c> with spaces around the <c>=</c>; the travel-map hover writes
    /// <c>&lt;span class ='blu'&gt;</c> with a space only before it. Both are reproduced, because
    /// <see cref="PFE.UI.Common.PfeTextFormatter"/> already matches the attribute with optional
    /// whitespace and a normalising "fix" here would hide a real difference from the Oracle for no
    /// gain.</para>
    /// </summary>
    public static class PipPageMarkup
    {
        /// <summary>AS3 <c>PipPage.yel</c> — the numbers and keyword colour (<c>PipPage.as:192-195</c>).</summary>
        public static string Yellow(object value) => "<span class = 'yel'>" + value + "</span>";

        /// <summary>AS3 <c>PipPage.red</c> — the "this will hurt" colour (<c>PipPage.as:197-200</c>).</summary>
        public static string Red(object value) => "<span class = 'red'>" + value + "</span>";

        /// <summary>AS3 <c>PipPage.blue</c> — note the class is <c>blu</c>, not <c>blue</c>
        /// (<c>PipPage.as:212-215</c>, and the page stylesheet at <c>:163-190</c> defines <c>.blu</c>).</summary>
        public static string Blue(object value) => "<span class = 'blu'>" + value + "</span>";

        /// <summary>The travel-map hover's own inline blue — <c>&lt;span class ='blu'&gt;</c>
        /// (<c>PipPageInfo.as:380</c>). Separate from <see cref="Blue"/> because the Oracle writes them
        /// with different spacing.</summary>
        public static string InlineBlue(string value) => "<span class ='blu'>" + value + "</span>";

        /// <summary>The travel-map hover's orange (<c>PipPageInfo.as:384</c>, <c>:388</c>).</summary>
        public static string InlineOrange(string value) => "<span class ='or'>" + value + "</span>";

        /// <summary>The travel-map hover's yellow (<c>PipPageInfo.as:392</c>, <c>:396</c>).</summary>
        public static string InlineYellow(string value) => "<span class ='yel'>" + value + "</span>";

        /// <summary>The journal's "this objective is done" dimmer (<c>PipPage.as:1331</c>, <c>:1363</c>).</summary>
        public static string Dark(string value) => "<span class = 'dark'>" + value + "</span>";
    }
}
