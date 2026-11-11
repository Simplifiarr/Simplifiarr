using System.Security.AccessControl;
using System.Security.Principal;

namespace Simplifiarr.Core.Utilities;

/// <summary>
/// Reads and repairs a file's Windows permissions. Only permissions are ever
/// changed, never the file's contents, so hardlinks, seeding and piece
/// checks are unaffected.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class FilePermissions
{
    // Folders can store a permission in a short "generic" form that Windows
    // expands when it hands it down to a file. These are the expansions for
    // files, so the two can be compared.
    private const int GenericRead = unchecked((int)0x80000000);
    private const int GenericWrite = 0x40000000;
    private const int GenericExecute = 0x20000000;
    private const int GenericAll = 0x10000000;

    private const int FileGenericRead = 0x120089;
    private const int FileGenericWrite = 0x120116;
    private const int FileGenericExecute = 0x1200A0;
    private const int FileAllAccess = 0x1F01FF;

    private const int Synchronize = 0x100000;

    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier AuthenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier ServiceGroup = new(WellKnownSidType.ServiceSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>
    /// The account and the groups it is known to belong to, for checking
    /// whether it can read a file. Only groups every account of its kind is
    /// in are included, so a "can't read" answer can be wrong only when
    /// access comes through a group of the user's own making.
    /// </summary>
    public static IReadOnlyCollection<SecurityIdentifier>? IdentitiesFor(
        string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return null;
        }

        SecurityIdentifier sid;

        try
        {
            sid = account.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? new SecurityIdentifier(account)
                : (SecurityIdentifier)new NTAccount(NormalizeAccount(account)).Translate(typeof(SecurityIdentifier));
        }
        catch (Exception exception) when (exception is IdentityNotMappedException or ArgumentException or SystemException)
        {
            return null;
        }

        var identities = new List<SecurityIdentifier> { sid, Everyone, AuthenticatedUsers, Users };

        if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid))
        {
            identities.Add(Administrators);
        }

        if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            sid.IsWellKnown(WellKnownSidType.NetworkServiceSid) ||
            sid.IsWellKnown(WellKnownSidType.LocalServiceSid))
        {
            identities.Add(ServiceGroup);
        }

        return identities;
    }

    /// <summary>
    /// Whether any of these identities is allowed to read the file and none
    /// is denied.
    /// </summary>
    public static bool CanRead(
        FileSecurity security,
        IReadOnlyCollection<SecurityIdentifier> identities)
    {
        var allowed = false;

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference is not SecurityIdentifier sid ||
                !identities.Contains(sid) ||
                (Expand((int)rule.FileSystemRights) & (int)FileSystemRights.ReadData) == 0)
            {
                continue;
            }

            if (rule.AccessControlType == AccessControlType.Deny)
            {
                return false;
            }

            allowed = true;
        }

        return allowed;
    }

    /// <summary>
    /// Whether the file's permissions are the ones its folder hands down:
    /// inheritance is on, nothing extra was added to the file itself, and
    /// what it inherited matches what the folder gives files today.
    /// </summary>
    public static bool MatchesFolder(
        FileSecurity file,
        DirectorySecurity folder)
    {
        if (file.AreAccessRulesProtected)
        {
            return false;
        }

        var have = new HashSet<string>(StringComparer.Ordinal);

        foreach (FileSystemAccessRule rule in file.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (!rule.IsInherited)
            {
                return false;
            }

            have.Add(Key(rule));
        }

        var want = new HashSet<string>(StringComparer.Ordinal);

        foreach (FileSystemAccessRule rule in folder.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if ((rule.InheritanceFlags & InheritanceFlags.ObjectInherit) != 0)
            {
                want.Add(Key(rule));
            }
        }

        return have.SetEquals(want);
    }

    /// <summary>
    /// Makes a file take its folder's permissions again: turns inheritance
    /// on and removes anything set on the file itself (the same as
    /// "icacls file /reset"). Returns the permissions it had, so this can be
    /// undone.
    /// </summary>
    public static string Reset(
        string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        var before = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        // A fresh, empty list that inherits: Windows then fills in what the
        // folder hands down.
        var fresh = new FileSecurity();

        fresh.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);

        info.SetAccessControl(fresh);

        return before;
    }

    /// <summary>
    /// Puts back permissions saved by <see cref="Reset"/>.
    /// </summary>
    public static void Restore(
        string path,
        string sddl)
    {
        var security = new FileSecurity();

        security.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);

        new FileInfo(path).SetAccessControl(security);
    }

    /// <summary>
    /// Gives the media server's account read access to this one file,
    /// leaving everything else as it is. Used only when resetting the file
    /// to its folder's permissions wouldn't be enough.
    /// </summary>
    public static void GrantRead(
        string path,
        SecurityIdentifier account)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();

        security.AddAccessRule(
            new FileSystemAccessRule(
                account,
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
                AccessControlType.Allow));

        info.SetAccessControl(security);
    }

    private static string Key(FileSystemAccessRule rule) =>
        $"{rule.IdentityReference.Value}|{rule.AccessControlType}|{Expand((int)rule.FileSystemRights) & ~Synchronize}";

    private static int Expand(int rights)
    {
        var specific = rights & 0x0FFFFFFF;

        if ((rights & GenericRead) != 0) specific |= FileGenericRead;
        if ((rights & GenericWrite) != 0) specific |= FileGenericWrite;
        if ((rights & GenericExecute) != 0) specific |= FileGenericExecute;
        if ((rights & GenericAll) != 0) specific |= FileAllAccess;

        return specific;
    }

    // Services list their accounts as e.g. "NT Authority\NetworkService";
    // Windows' own name for it has a space.
    private static string NormalizeAccount(string account) =>
        account.Trim() switch
        {
            var name when name.Equals(@"NT Authority\NetworkService", StringComparison.OrdinalIgnoreCase) => @"NT AUTHORITY\NETWORK SERVICE",
            var name when name.Equals(@"NT Authority\LocalService", StringComparison.OrdinalIgnoreCase) => @"NT AUTHORITY\LOCAL SERVICE",
            var name when name.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) => @"NT AUTHORITY\SYSTEM",
            var name when name.StartsWith(@".\", StringComparison.Ordinal) => Environment.MachineName + name[1..],
            var name => name
        };
}
