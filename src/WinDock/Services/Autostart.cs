using Microsoft.Win32;
using System.Buffers.Binary;
using System.Security;

namespace WinDock.Services;

public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string PolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    const string Name = "WinDock";

    public static string Command(string exe) => $"\"{exe}\" --tray";

    public enum Approval { Unrecorded, Enabled, Disabled, Unknown }
    public enum Policy { Unconfigured, Allowed, Disabled, Unknown }

    public sealed record State(bool? Registered, bool MatchesCurrentExecutable, Approval WindowsApproval, Policy RunPolicy,
        bool WriteFailed = false)
    {
        public bool? WindowsAllowsStartup => RunPolicy == Policy.Disabled || WindowsApproval == Approval.Disabled ? false
            : RunPolicy == Policy.Unknown || WindowsApproval is Approval.Unknown or Approval.Unrecorded ? null : true;
        public bool Enabled => Registered == true && MatchesCurrentExecutable && WindowsAllowsStartup == true;
        public bool CanChange => Registered is not null && RunPolicy is not (Policy.Disabled or Policy.Unknown)
            && WindowsApproval != Approval.Unknown;
        public bool NeedsWindowsSettings => WindowsApproval is Approval.Disabled or Approval.Unknown
            || Registered == true && WindowsAllowsStartup is null;
        public string StatusKey => WriteFailed ? "startupChangeFailed"
            : RunPolicy == Policy.Disabled ? "startupDisabledByPolicy"
            : WindowsApproval == Approval.Disabled ? "startupDisabledByWindows"
            : Registered is null || RunPolicy == Policy.Unknown || WindowsApproval == Approval.Unknown ? "startupUnknown"
            : Registered == false ? ""
            : !MatchesCurrentExecutable ? "startupDifferentVersion"
            : WindowsApproval == Approval.Unrecorded ? "startupUnconfirmed" : "";
    }

    public static State Read()
    {
        bool? registered = null;
        var matches = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var value = key?.GetValue(Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            registered = value is not null;
            matches = value is string command && Environment.ProcessPath is { } exe
                && command.Equals(Command(exe), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (RegistryFailure(e)) { }
        return new(registered, matches, ReadApproval(), ReadPolicy());
    }

    static Approval ReadApproval()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovalKey);
            var value = key?.GetValue(Name);
            if (value is null) return Approval.Unrecorded;
            // StartupApproved is not a public registry contract. Only decode
            // recognized records; preserve every byte, including unknown states.
            // Primary implementation and limitations: docs/startup-behavior.md.
            if (value is not byte[] { Length: 12 } bytes) return Approval.Unknown;
            var state = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (state == 3) return Approval.Disabled;
            if (state is not (0 or 2) || bytes.AsSpan(4).ContainsAnyExcept((byte)0)) return Approval.Unknown;
            return Approval.Enabled;
        }
        catch (Exception e) when (RegistryFailure(e)) { return Approval.Unknown; }
    }

    static Policy ReadPolicy()
    {
        // ADMX_Logon defines different value names for Computer and User
        // Configuration; an explicitly configured computer value takes priority.
        var machine = ReadPolicyValue(Registry.LocalMachine, "DisableLocalMachineRun");
        return machine != Policy.Unconfigured ? machine : ReadPolicyValue(Registry.CurrentUser, "DisableCurrentUserRun");
    }

    static Policy ReadPolicyValue(RegistryKey root, string name)
    {
        try
        {
            using var key = root.OpenSubKey(PolicyKey);
            return key?.GetValue(name) switch
            {
                null => Policy.Unconfigured,
                int value when value == 0 => Policy.Allowed,
                int value when value == 1 => Policy.Disabled,
                _ => Policy.Unknown
            };
        }
        catch (Exception e) when (RegistryFailure(e)) { return Policy.Unknown; }
    }

    static bool RegistryFailure(Exception e) => e is IOException or UnauthorizedAccessException or SecurityException;

    public static State SetEnabled(bool enabled)
    {
        var before = Read();
        if (!before.CanChange) return before;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe) key.SetValue(Name, Command(exe), RegistryValueKind.String);
            else if (!enabled) key.DeleteValue(Name, throwOnMissingValue: false);
            var after = Read();
            return after with { WriteFailed = enabled ? !after.MatchesCurrentExecutable : after.Registered != false };
        }
        catch (Exception e) when (RegistryFailure(e)) { return Read() with { WriteFailed = true }; }
    }

    public static bool Enabled
    {
        get => Read().Enabled;
        set => SetEnabled(value);
    }
}
