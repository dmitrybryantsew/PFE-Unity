using System;
using System.Reflection;

namespace PFE.Core
{
    /// <summary>
    /// Who is allowed to write a piece of Sim state, and whether it crosses the network.
    ///
    /// Used purely as a compile-time / lint-time authority map. The attributes carry no runtime
    /// cost and no behaviour; they exist so that "may a client call this?" is answerable by
    /// reading the declaration, and so a test can assert the map stays consistent.
    ///
    /// From docs/Roadmap/05_P4_MULTIPLAYER_AND_MODDING_FOUNDATIONS.md, milestone 4.1.
    /// </summary>
    public enum SimAuthority
    {
        /// <summary>Not marked. A lint test should fail on this until the map is complete.</summary>
        Unspecified = 0,

        /// <summary>
        /// The host owns this state and is the only side allowed to change it.
        /// Clients receive the result; they never originate it.
        /// </summary>
        Authoritative = 1,

        /// <summary>
        /// A client may simulate this locally to hide latency, but must accept a host correction
        /// and roll back. Requires a reconciliation path (P4.3).
        /// </summary>
        ClientPredicted = 2,

        /// <summary>
        /// Never replicated. Either derived locally on every machine, or purely cosmetic.
        /// Both host and client may compute it; the result is never sent.
        /// </summary>
        LocalOnly = 3,
    }

    /// <summary>
    /// Base attribute. Query via <see cref="AuthorityResolver"/>; the three derived attributes
    /// are the ones used at declaration sites.
    /// </summary>
    [AttributeUsage(
        AttributeTargets.Interface | AttributeTargets.Class | AttributeTargets.Method |
        AttributeTargets.Property | AttributeTargets.Field,
        AllowMultiple = false,
        Inherited = true)]
    public class SimAuthorityAttribute : Attribute
    {
        public SimAuthority Authority { get; }

        /// <summary>Free-form justification. Recorded so the classification can be reviewed.</summary>
        public string Note { get; set; }

        public SimAuthorityAttribute(SimAuthority authority)
        {
            Authority = authority;
        }
    }

    /// <summary>Host-only mutation. Clients receive the outcome, never originate it.</summary>
    public sealed class AuthoritativeAttribute : SimAuthorityAttribute
    {
        public AuthoritativeAttribute() : base(SimAuthority.Authoritative) { }
    }

    /// <summary>Client may predict, must accept rollback.</summary>
    public sealed class ClientPredictedAttribute : SimAuthorityAttribute
    {
        public ClientPredictedAttribute() : base(SimAuthority.ClientPredicted) { }
    }

    /// <summary>Computed locally, never sent over the network.</summary>
    public sealed class LocalOnlyAttribute : SimAuthorityAttribute
    {
        public LocalOnlyAttribute() : base(SimAuthority.LocalOnly) { }
    }

    /// <summary>
    /// Reads authority back off a member.
    ///
    /// CAVEAT: attributes on interface members are NOT inherited by implementing classes, so
    /// query the interface declaration (for example <c>typeof(ITileQueryService).GetMethod(...)</c>),
    /// not the concrete type.
    /// </summary>
    public static class AuthorityResolver
    {
        public static SimAuthority Of(MemberInfo member)
        {
            if (member == null) return SimAuthority.Unspecified;
            SimAuthorityAttribute attribute = member.GetCustomAttribute<SimAuthorityAttribute>(inherit: true);
            return attribute != null ? attribute.Authority : SimAuthority.Unspecified;
        }
    }
}
