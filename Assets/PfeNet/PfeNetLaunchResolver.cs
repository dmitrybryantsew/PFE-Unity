using PFE.Core;

namespace PFE.Net
{
    /// <summary>Which configuration source owns this process's session.</summary>
    public enum PfeNetLaunchSource
    {
        None = 0,
        CommandLine = 1,
        Menu = 2,
    }

    /// <summary>
    /// Turns either configuration source into one <see cref="PfeNetConfig"/>. Pure: no Unity, no
    /// FishNet, no globals. The adapter's <c>Bootstrap</c> and menu watcher only read its result, so
    /// the precedence and the validation are executable in the offline wall harness.
    ///
    /// <para><b>Why the menu request is re-validated here.</b> <c>PfeNetLaunchRequest</c> has a public
    /// constructor and crosses an assembly boundary. A caller in another assembly can build one
    /// directly with an out-of-range port, and a raw <c>(ushort)</c> cast would truncate 65536 to 0 —
    /// a session that binds an arbitrary port while the UI shows a different one. Validation belongs
    /// at the point where the value becomes a port.</para>
    /// </summary>
    public static class PfeNetLaunchResolver
    {
        public const string NoSwitchReason = "no -pfe-net-host/-pfe-net-client switch";

        /// <summary>An explicit launch switch is an operator override and always wins; a menu request
        /// is used only when the command line names no role.</summary>
        public static PfeNetLaunchSource SelectSource(bool commandLineHasRole, bool menuHasRequest)
        {
            if (commandLineHasRole) return PfeNetLaunchSource.CommandLine;
            return menuHasRequest ? PfeNetLaunchSource.Menu : PfeNetLaunchSource.None;
        }

        /// <summary>Returns false both for an invalid launch string and for a launch string with no
        /// role switch. <paramref name="reason"/> distinguishes them: only the first is an error.</summary>
        public static bool TryResolveCommandLine(string[] args, out PfeNetConfig config, out string reason)
        {
            config = null;

            PfeNetConfig parsed;
            if (!PfeNetCommandLine.TryParse(args, out parsed))
            {
                reason = "invalid -pfe-net-* arguments";
                return false;
            }

            if (parsed.Role == PfeNetRole.Off)
            {
                reason = NoSwitchReason;
                return false;
            }

            config = parsed;
            reason = "command line";
            return true;
        }

        public static bool TryResolveMenu(PfeNetLaunchRequest request, out PfeNetConfig config, out string reason)
        {
            config = null;
            if (request == null)
            {
                reason = "no menu request";
                return false;
            }

            PfeNetLaunchRequest rebuilt;
            string error;
            if (!PfeNetLaunchRules.TryCreate(request.Role, request.Address, request.Port,
                    out rebuilt, out error))
            {
                reason = "invalid menu request: " + error;
                return false;
            }

            config = new PfeNetConfig
            {
                Role = rebuilt.Role == PfeNetLaunchRole.Host ? PfeNetRole.Host : PfeNetRole.Client,
                Address = rebuilt.Address,
                Port = (ushort)rebuilt.Port,
            };
            reason = "menu";
            return true;
        }
    }
}
