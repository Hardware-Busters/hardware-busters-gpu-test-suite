using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace GpuSuite.Measurement.Monitoring.FamilyTransport;

internal sealed class CurrentUserOnlySecurity<TRights> : NativeObjectSecurity where TRights : struct
{
    private CurrentUserOnlySecurity() : base(false, ResourceType.KernelObject) { }

    public override Type AccessRightType => typeof(TRights);
    public override Type AccessRuleType => typeof(AccessRule<TRights>);
    public override Type AuditRuleType => typeof(AuditRule<TRights>);

    internal static CurrentUserOnlySecurity<TRights> For(SecurityIdentifier user)
    {
        var security = new CurrentUserOnlySecurity<TRights>();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new AccessRule<TRights>(user, Rights((int)TelemetryKernelRights.FullControl), AccessControlType.Allow));
        return security;
    }

    internal void ApplyTo(SafeHandle handle) => Persist(handle, AccessControlSections.Access);

    public override AccessRule AccessRuleFactory(IdentityReference identityReference, int accessMask,
        bool isInherited, InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AccessControlType type)
        => new AccessRule<TRights>(identityReference, Rights(accessMask), type);

    public override AuditRule AuditRuleFactory(IdentityReference identityReference, int accessMask,
        bool isInherited, InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AuditFlags flags)
        => new AuditRule<TRights>(identityReference, Rights(accessMask), flags);

    private static TRights Rights(int mask) => (TRights)Enum.ToObject(typeof(TRights), mask);
}
