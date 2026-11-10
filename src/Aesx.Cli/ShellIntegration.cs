// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Aesx.Cli;

/// <summary><c>aesx register</c> and <c>aesx unregister</c>: Windows Explorer integration for the current user.</summary>
internal static class ShellIntegration
{
    public static int Run(Options options)
    {
        string command = options.Command.ToString().ToLowerInvariant();
        if (options.Inputs.Count > 0 || options.Output is not null || options.Password is not null
            || options.Force || options.DeleteInput || options.IterationsSpecified)
        {
            throw new UsageException($"'{command}' takes no arguments");
        }
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException($"'{command}' sets up the Windows Explorer menus; there is nothing for it to do on this system");

        using RegistryKey classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        TextWriter status = options.Quiet ? TextWriter.Null : Console.Out;

        if (options.Command == Command.Unregister)
        {
            new ShellRegistration(classes, executable: "").Unregister();
            ShellRegistration.TellExplorer();
            status.WriteLine("Removed aesx from the Explorer menus and from .aesx files.");
            return Program.ExitSuccess;
        }

        string executable = Environment.ProcessPath ?? "";
        if (!string.Equals(Path.GetFileName(executable), "aesx.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("'register' has to be run by aesx.exe itself, because the menu entries point at it");
        if (executable.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"aesx.exe is running from a temporary folder ({TextSafety.Display(Path.GetDirectoryName(executable) ?? "")}), probably straight out of the downloaded archive; unpack it somewhere permanent and run 'aesx register' there");

        string? other = new ShellRegistration(classes, executable).Register();
        ShellRegistration.TellExplorer();
        status.WriteLine($"""
            Registered for the current user (no administrator rights involved):
              right-click files or folders   Encrypt with aesx
              right-click .aesx files        Decrypt with aesx (also what a double-click does)
            Any number of items can be selected; the password is asked once.
            On Windows 11 the entries are under "Show more options".
            They point at {TextSafety.Display(executable)}
            Run 'aesx register' again after moving it, and 'aesx unregister' to remove everything.
            """);
        if (other is not null)
            Console.Error.WriteLine($"note: .aesx files already belong to another program ({TextSafety.Display(other)}), so a double-click still starts that one; aesx is in their \"Open with\" list");
        return Program.ExitSuccess;
    }
}

/// <summary>
/// What registering writes, all of it in the current user's own registry hive: the
/// <c>.aesx</c> file type with "Decrypt with aesx", "Encrypt with aesx" for every file and every
/// folder, and the class through which Explorer hands a whole selection to one aesx process. The
/// hive is a parameter so that tests can use a scratch key instead of somebody's real settings.
/// </summary>
/// <param name="classes">Stands in for <c>HKEY_CURRENT_USER\Software\Classes</c>.</param>
/// <param name="executable">Full path of aesx.exe; not needed to unregister.</param>
[SupportedOSPlatform("windows")]
internal sealed class ShellRegistration(RegistryKey classes, string executable)
{
    public const string FileType = "aesx.EncryptedFile";

    /// <summary>The menu entry's registry name, which is also what Explorer reports as the verb.</summary>
    public const string EncryptVerb = "aesx";
    public const string DecryptVerb = "open";

    private const string OpenWith = "OpenWithProgids";
    private static readonly string[] s_menuOwners = ["*", "Directory"]; // every file, every folder
    private static readonly string s_class = ExplorerSelection.ClassId.ToString("B").ToUpperInvariant();

    /// <summary>Writes the entries, replacing earlier ones of ours.</summary>
    /// <returns>The file type that owns <c>.aesx</c> if it is not ours (it is left alone), otherwise null.</returns>
    public string? Register()
    {
        // Explorer starts this, adding " -Embedding", whenever one of the entries is chosen, and
        // passes the selection to it; see ExplorerSelection.
        using (RegistryKey server = classes.CreateSubKey($@"CLSID\{s_class}"))
        {
            server.SetValue(null, "aesx");
            using RegistryKey local = server.CreateSubKey("LocalServer32");
            local.SetValue(null, $"\"{executable}\"");
        }

        using (RegistryKey type = classes.CreateSubKey(FileType))
        {
            type.SetValue(null, "aesx encrypted file");
            using RegistryKey shell = type.CreateSubKey("shell");
            shell.SetValue(null, DecryptVerb);
            WriteVerb(shell, DecryptVerb, "Decrypt with aesx", "decrypt", executable);
        }

        string? other = null;
        using (RegistryKey extension = classes.CreateSubKey(AesxFormat.FileExtension))
        {
            string? owner = extension.GetValue(null) as string;
            if (string.IsNullOrEmpty(owner) || owner == FileType) extension.SetValue(null, FileType);
            else other = owner;
            using RegistryKey openWith = extension.CreateSubKey(OpenWith);
            openWith.SetValue(FileType, "");
        }

        foreach (string owner in s_menuOwners)
        {
            using RegistryKey shell = classes.CreateSubKey($@"{owner}\shell");
            WriteVerb(shell, EncryptVerb, "Encrypt with aesx", "encrypt", executable);
        }
        return other;
    }

    /// <summary>One menu entry: its label, and the two ways of carrying it out.</summary>
    internal static void WriteVerb(RegistryKey shell, string name, string label, string command, string executable)
    {
        using RegistryKey verb = shell.CreateSubKey(name);
        verb.SetValue(null, label);

        // "Player": offered for a selection of any size, and invoked once for all of it.
        verb.SetValue("MultiSelectModel", "Player");
        using RegistryKey line = verb.CreateSubKey("command");
        line.SetValue("DelegateExecute", s_class);

        // For anything that starts the verb the old way, one item at a time. A file name cannot
        // contain a quote, so quoting the two paths is all the escaping there is.
        line.SetValue(null, $"\"{executable}\" {command} \"%1\"");
    }

    /// <summary>Removes what <see cref="Register"/> wrote and nothing else; missing pieces are not an error.</summary>
    public void Unregister()
    {
        foreach (string owner in s_menuOwners)
        {
            classes.DeleteSubKeyTree($@"{owner}\shell\{EncryptVerb}", throwOnMissingSubKey: false);
        }
        classes.DeleteSubKeyTree(FileType, throwOnMissingSubKey: false);
        classes.DeleteSubKeyTree($@"CLSID\{s_class}", throwOnMissingSubKey: false);

        // Keys that Register may have had to create on the way and that now hold nothing.
        foreach (string owner in s_menuOwners)
        {
            DeleteIfEmpty($@"{owner}\shell");
            DeleteIfEmpty(owner);
        }
        DeleteIfEmpty("CLSID");

        bool empty = false;
        using (RegistryKey? extension = classes.OpenSubKey(AesxFormat.FileExtension, writable: true))
        {
            if (extension is not null)
            {
                if (extension.GetValue(null) as string == FileType) extension.DeleteValue("", throwOnMissingValue: false);
                bool openWithEmpty = false;
                using (RegistryKey? openWith = extension.OpenSubKey(OpenWith, writable: true))
                {
                    openWith?.DeleteValue(FileType, throwOnMissingValue: false);
                    openWithEmpty = openWith is { ValueCount: 0, SubKeyCount: 0 };
                }
                if (openWithEmpty) extension.DeleteSubKey(OpenWith, throwOnMissingSubKey: false);
                empty = extension is { ValueCount: 0, SubKeyCount: 0 };
            }
        }
        if (empty) classes.DeleteSubKey(AesxFormat.FileExtension, throwOnMissingSubKey: false);
    }

    private void DeleteIfEmpty(string name)
    {
        bool empty;
        using (RegistryKey? key = classes.OpenSubKey(name))
        {
            empty = key is { ValueCount: 0, SubKeyCount: 0 };
        }
        if (empty) classes.DeleteSubKey(name, throwOnMissingSubKey: false);
    }

    /// <summary>Asks Explorer to re-read the file types, so the change shows without signing out.</summary>
    public static void TellExplorer() => ExplorerSelection.NotifyAssociationsChanged();
}

/// <summary>
/// Receives what was selected in Explorer when one of the menu entries is chosen.
/// </summary>
/// <remarks>
/// <para>
/// A menu entry that names a program is started once for every selected item, which for an
/// encryption tool means a password prompt per file. The alternative Windows offers needs no
/// code inside Explorer: the entry names a class (<c>DelegateExecute</c>), Windows starts the
/// program registered for it with <c>-Embedding</c>, the program announces the class, and Explorer
/// passes it the verb and the whole selection in one go. That is all this type does: announce,
/// wait, take the paths. The work then happens the way it does for names on a command line.
/// </para>
/// <para>
/// The interfaces are marshalled by code generated at compile time, so nothing depends on the
/// runtime's built-in COM support, which a trimmed build does not include.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class ExplorerSelection
{
    /// <summary>The class Explorer asks for; the same value is written to the registry.</summary>
    public static readonly Guid ClassId = new("1715CB39-E2F1-4879-8D33-257E7E2ABD38");

    private const uint Multithreaded = 0x0;
    private const uint LocalServer = 0x4;
    private const uint SingleUse = 0x0;
    private const uint AssociationsChanged = 0x0800_0000;

    /// <summary>
    /// Waits for Explorer to deliver a selection. Returns the verb's registry name (null if
    /// Explorer did not say) and the file-system paths selected, or null if nobody came.
    /// </summary>
    public static (string? Verb, List<string> Paths)? Receive(TimeSpan patience)
    {
        // Calls from Explorer arrive on the runtime's own threads, which needs the
        // multithreaded apartment; the main thread of a .NET program is in it already.
        _ = CoInitializeEx(0, Multithreaded);

        var wrappers = new StrategyBasedComWrappers();
        using var command = new ExplorerCommand();
        nint factory = wrappers.GetOrCreateComInterfaceForObject(new ExplorerCommandFactory(command, wrappers), CreateComInterfaceFlags.None);
        try
        {
            Marshal.ThrowExceptionForHR(CoRegisterClassObject(in ClassId, factory, LocalServer, SingleUse, out uint registration));
            bool executed = command.Executed.Wait(patience);
            _ = CoRevokeClassObject(registration);
            return executed ? (command.Verb, command.Paths) : null;
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    public static void NotifyAssociationsChanged() => SHChangeNotify(AssociationsChanged, 0, 0, 0);

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CoInitializeEx(nint reserved, uint model);

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CoRegisterClassObject(in Guid classId, nint unknown, uint context, uint flags, out uint registration);

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CoRevokeClassObject(uint registration);

    [LibraryImport("shell32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}

/// <summary>The object Explorer talks to: it remembers the verb and the selection, and says when to start.</summary>
[GeneratedComClass]
[SupportedOSPlatform("windows")]
internal sealed partial class ExplorerCommand : IExecuteCommand, IObjectWithSelection, IInitializeCommand, IDisposable
{
    private const uint FileSystemPath = 0x80058000; // SIGDN_FILESYSPATH
    private const int NotImplemented = unchecked((int)0x80004001);

    public string? Verb { get; private set; }

    public List<string> Paths { get; } = [];

    public ManualResetEventSlim Executed { get; } = new();

    public void Initialize(string commandName, nint propertyBag) => Verb = commandName;

    public void SetSelection(IShellItemArray? selection)
    {
        Paths.Clear();
        if (selection is null) return;
        selection.GetCount(out uint count);
        for (uint i = 0; i < count; i++)
        {
            selection.GetItemAt(i, out IShellItem item);

            // Something selected that is not a file or a folder (a printer, a search result
            // without a location) has no path, and there is nothing aesx could do with it.
            if (item.GetDisplayName(FileSystemPath, out nint name) < 0 || name == 0) continue;
            try
            {
                if (Marshal.PtrToStringUni(name) is { Length: > 0 } path) Paths.Add(path);
            }
            finally
            {
                Marshal.FreeCoTaskMem(name);
            }
        }
    }

    public int GetSelection(in Guid interfaceId, out nint selection)
    {
        selection = 0;
        return NotImplemented;
    }

    public void SetKeyState(uint keyState)
    {
    }

    public void SetParameters(string? parameters)
    {
    }

    public void SetPosition(ScreenPoint point)
    {
    }

    public void SetShowWindow(int show)
    {
    }

    public void SetNoShowUI(bool noShowUI)
    {
    }

    public void SetDirectory(string? directory)
    {
    }

    /// <summary>Explorer waits for this to return, so it only passes the word on.</summary>
    public void Execute() => Executed.Set();

    public void Dispose() => Executed.Dispose();
}

/// <summary>Hands Explorer the one command object when it asks for an instance of the class.</summary>
[GeneratedComClass]
[SupportedOSPlatform("windows")]
internal sealed partial class ExplorerCommandFactory(ExplorerCommand command, StrategyBasedComWrappers wrappers) : IClassFactory
{
    private const int NoAggregation = unchecked((int)0x80040110);

    public int CreateInstance(nint outer, in Guid interfaceId, out nint instance)
    {
        instance = 0;
        if (outer != 0) return NoAggregation;
        nint unknown = wrappers.GetOrCreateComInterfaceForObject(command, CreateComInterfaceFlags.None);
        try
        {
            return Marshal.QueryInterface(unknown, in interfaceId, out instance);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    public int LockServer(bool hold) => 0;
}

/// <summary>POINT.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ScreenPoint
{
    public int X;
    public int Y;
}

[GeneratedComInterface]
[Guid("00000001-0000-0000-C000-000000000046")]
internal partial interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(nint outer, in Guid interfaceId, out nint instance);

    [PreserveSig]
    int LockServer([MarshalAs(UnmanagedType.Bool)] bool hold);
}

/// <summary>How a verb implemented outside Explorer is told what to do; the methods come in vtable order.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("7F9185B0-CB92-43C5-80A9-92277A4F7B54")]
internal partial interface IExecuteCommand
{
    void SetKeyState(uint keyState);
    void SetParameters(string? parameters);
    void SetPosition(ScreenPoint point);
    void SetShowWindow(int show);
    void SetNoShowUI([MarshalAs(UnmanagedType.Bool)] bool noShowUI);
    void SetDirectory(string? directory);
    void Execute();
}

[GeneratedComInterface]
[Guid("1C9CD5BB-98E9-4491-A60F-31AACC72B83C")]
internal partial interface IObjectWithSelection
{
    void SetSelection(IShellItemArray? selection);

    [PreserveSig]
    int GetSelection(in Guid interfaceId, out nint selection);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("85075ACF-231F-40EA-9610-D26B7B58F638")]
internal partial interface IInitializeCommand
{
    void Initialize(string commandName, nint propertyBag);
}

/// <summary>IShellItemArray; only the count and the items are used, the rest keeps raw pointers.</summary>
[GeneratedComInterface]
[Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
internal partial interface IShellItemArray
{
    void BindToHandler(nint bindContext, in Guid handler, in Guid interfaceId, out nint result);
    void GetPropertyStore(int flags, in Guid interfaceId, out nint result);
    void GetPropertyDescriptionList(nint keyType, in Guid interfaceId, out nint result);
    void GetAttributes(int flags, uint mask, out uint attributes);
    void GetCount(out uint count);
    void GetItemAt(uint index, out IShellItem item);
    void EnumItems(out nint enumerator);
}

/// <summary>IShellItem; only the display name is used.</summary>
[GeneratedComInterface]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
internal partial interface IShellItem
{
    void BindToHandler(nint bindContext, in Guid handler, in Guid interfaceId, out nint result);
    void GetParent(out nint parent);

    [PreserveSig]
    int GetDisplayName(uint form, out nint name);

    void GetAttributes(uint mask, out uint attributes);
    void Compare(nint other, uint hint, out int order);
}
