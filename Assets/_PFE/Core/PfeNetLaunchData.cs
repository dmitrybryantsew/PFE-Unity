using System;

namespace PFE.Core
{
    /// <summary>
    /// The role chosen in the main menu.
    ///
    /// <para><b>Why this is a <c>PFE.Core</c> type and not <c>PFE.Net.PfeNetRole</c>.</b> The menu lives
    /// under <c>PFE.View.asmdef</c>, while the FishNet adapter deliberately lives in the predefined
    /// <c>Assembly-CSharp</c> (no asmdef, so it can see FishNet.Runtime without an owner-only asmdef
    /// edit). An asmdef assembly can never reference a predefined one, so the menu cannot name
    /// <c>PfeNetRole</c>, <c>PfeNetConfig</c> or <c>PfeReplicationSession</c> at all. The two sides
    /// therefore share vocabulary through this static bridge and the adapter translates it.</para>
    ///
    /// <para><b>Keep the numeric values aligned with <c>PFE.Net.PfeNetRole</c>.</b> They are separate
    /// enums; the resolver maps by name, not by cast, so a renumbering here cannot silently change a
    /// role — but the literals are pinned by a wiring lint anyway.</para>
    /// </summary>
    public enum PfeNetLaunchRole
    {
        Off = 0,
        Host = 1,
        Client = 2,
    }

    /// <summary>One immutable, single-use LAN launch request. Plain data: no Unity, no FishNet.</summary>
    public sealed class PfeNetLaunchRequest
    {
        public readonly PfeNetLaunchRole Role;
        public readonly string Address;
        public readonly int Port;

        public PfeNetLaunchRequest(PfeNetLaunchRole role, string address, int port)
        {
            Role = role;
            Address = address;
            Port = port;
        }
    }

    /// <summary>
    /// Validation of a menu-driven LAN launch. Pure and offline-testable, because a text field is the
    /// only configuration a built player has: a bad address must be refused in the menu rather than
    /// becoming a connection timeout that looks like a networking bug.
    /// </summary>
    public static class PfeNetLaunchRules
    {
        public const int DefaultPort = 7770;
        public const int MinPort = 1;
        public const int MaxPort = 65535;
        public const int MaxAddressLength = 253;
        public const string LoopbackAddress = "localhost";

        public static bool IsValidRole(PfeNetLaunchRole role)
        {
            return role == PfeNetLaunchRole.Host || role == PfeNetLaunchRole.Client;
        }

        public static bool IsValidPort(int port)
        {
            return port >= MinPort && port <= MaxPort;
        }

        /// <summary>
        /// A host binds <c>0.0.0.0</c> inside Tugboat and never reads a client address, so a host
        /// request needs none. A join <b>must</b> have an explicit one: an empty field would otherwise
        /// fall back to loopback, the client would connect to itself (or to nothing), and the overlay
        /// would report a healthy transport with no peer.
        /// </summary>
        public static bool TryNormalizeAddress(PfeNetLaunchRole role, string raw, out string address)
        {
            address = null;
            if (!IsValidRole(role)) return false;

            if (role == PfeNetLaunchRole.Host)
            {
                address = LoopbackAddress;
                return true;
            }

            if (string.IsNullOrWhiteSpace(raw)) return false;
            string trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxAddressLength) return false;
            if (trimmed[0] == '-') return false;   // a launch flag pasted into the address field

            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                // A colon means "host:port" was typed into the address field; the port has its own
                // input. Tugboat's LAN bind is IPv4, so an IPv6 literal is refused here too.
                if (char.IsWhiteSpace(c) || char.IsControl(c) || c == ':') return false;
            }

            address = trimmed;
            return true;
        }

        public static bool TryCreate(PfeNetLaunchRole role, string address, int port,
            out PfeNetLaunchRequest request, out string error)
        {
            request = null;
            if (!IsValidRole(role))
            {
                error = "choose Host or Join";
                return false;
            }

            string normalized;
            if (!TryNormalizeAddress(role, address, out normalized))
            {
                error = "enter the host's LAN address";
                return false;
            }

            if (!IsValidPort(port))
            {
                error = "port must be 1..65535";
                return false;
            }

            request = new PfeNetLaunchRequest(role, normalized, port);
            error = "none";
            return true;
        }
    }

    /// <summary>
    /// Static one-shot bridge from the main menu (<c>PFE.View.asmdef</c>) to the FishNet adapter
    /// (<c>Assembly-CSharp</c>). This is the same seam <c>GameBootData</c> uses for new-game settings:
    /// the menu writes, the game-scene bootstrap consumes.
    ///
    /// <para><b>Why consume-and-clear instead of a mode flag.</b> A mode flag survives the scene load
    /// and a later return-to-title; the next ordinary single-player "New game" would then silently
    /// start a network session. <c>ConsumePending()</c> makes the request a boot directive that can be
    /// acted on exactly once, and <c>ClearPending()</c> gives the normal-game path an explicit way to
    /// discard a request whose scene load never happened.</para>
    /// </summary>
    public static class PfeNetLaunchData
    {
        private static PfeNetLaunchRequest _pending;

        public static bool HasPending => _pending != null;
        public static string LastError { get; private set; } = "none";
        public static int RequestCount { get; private set; }

        /// <summary>Validates and stores a request. Every attempt replaces the previous directive, so
        /// a failed attempt cannot leave an older, now-wrong role pending.</summary>
        public static bool TryRequest(PfeNetLaunchRole role, string address, int port)
        {
            _pending = null;

            PfeNetLaunchRequest request;
            string error;
            if (!PfeNetLaunchRules.TryCreate(role, address, port, out request, out error))
            {
                LastError = error;
                return false;
            }

            _pending = request;
            LastError = "none";
            RequestCount++;
            return true;
        }

        public static PfeNetLaunchRequest PeekPending()
        {
            return _pending;
        }

        public static PfeNetLaunchRequest ConsumePending()
        {
            PfeNetLaunchRequest request = _pending;
            _pending = null;
            return request;
        }

        public static void ClearPending()
        {
            _pending = null;
        }
    }
}
