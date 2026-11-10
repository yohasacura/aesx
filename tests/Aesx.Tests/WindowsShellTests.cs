// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using System.Text;
using Aesx.Cli;
using Microsoft.Win32;

namespace Aesx.Tests;

/// <summary>
/// The Windows Explorer integration: what registering writes, the password window, and a
/// selection handed over by the Windows shell itself. Every test is a no-op on other systems.
/// </summary>
/// <remarks>
/// Nothing here touches the real menu entries. Registry tests work on a scratch key; the
/// end-to-end tests register a file extension that exists for the length of one test, and stand
/// aside altogether on a machine where aesx is registered for real.
/// </remarks>
public class WindowsShellTests
{
    private const string Pw = "menu-password ✓";
    private const string Class = "{1715CB39-E2F1-4879-8D33-257E7E2ABD38}";
    private const string Executable = @"C:\Program Files\aesx tools\aesx.exe";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(false, 6)]
    [InlineData(true, 8)]
    public void TheDialogTemplateIsWellFormed(bool confirm, int controls)
    {
        if (!OperatingSystem.IsWindows()) return;

        byte[] template = PasswordDialog.BuildTemplate("Encrypt quarterly report (final).pdf", confirm);

        // DLGTEMPLATE: style, extended style, number of controls, x, y, cx, cy, menu, class, title.
        Assert.Equal(controls, BinaryPrimitives.ReadUInt16LittleEndian(template.AsSpan(8)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(template.AsSpan(18))); // no menu
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(template.AsSpan(20))); // standard dialog class
        Assert.Equal("aesx\0", Encoding.Unicode.GetString(template.AsSpan(22, 10)));
        Assert.Contains("Segoe UI", Encoding.Unicode.GetString(template), StringComparison.Ordinal);
        Assert.Contains("Encrypt quarterly report (final).pdf", Encoding.Unicode.GetString(template), StringComparison.Ordinal);
    }

    [Fact]
    public void RegisteringWritesTheMenuEntriesAndUnregisteringLeavesNoTrace()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var scratch = new ScratchKey();
        string? Value(string key, string? name)
        {
            using RegistryKey? opened = scratch.Key.OpenSubKey(key);
            return opened?.GetValue(name) as string;
        }

        Assert.Null(new ShellRegistration(scratch.Key, Executable).Register());

        // Explorer starts the program behind this class and hands it the selection.
        Assert.Equal($"\"{Executable}\"", Value($@"CLSID\{Class}\LocalServer32", null));
        foreach (string owner in new[] { "*", "Directory" })
        {
            Assert.Equal("Encrypt with aesx", Value($@"{owner}\shell\aesx", null));
            Assert.Equal("Player", Value($@"{owner}\shell\aesx", "MultiSelectModel")); // any number of items, one invocation
            Assert.Equal(Class, Value($@"{owner}\shell\aesx\command", "DelegateExecute"));
            Assert.Equal($"\"{Executable}\" encrypt \"%1\"", Value($@"{owner}\shell\aesx\command", null));
        }
        Assert.Equal("aesx.EncryptedFile", Value(".aesx", null));
        Assert.Equal("", Value(@".aesx\OpenWithProgids", "aesx.EncryptedFile"));
        Assert.Equal("open", Value(@"aesx.EncryptedFile\shell", null));
        Assert.Equal("Decrypt with aesx", Value(@"aesx.EncryptedFile\shell\open", null));
        Assert.Equal("Player", Value(@"aesx.EncryptedFile\shell\open", "MultiSelectModel"));
        Assert.Equal(Class, Value(@"aesx.EncryptedFile\shell\open\command", "DelegateExecute"));
        Assert.Equal($"\"{Executable}\" decrypt \"%1\"", Value(@"aesx.EncryptedFile\shell\open\command", null));

        // Registering again after a move just points everything at the new place.
        Assert.Null(new ShellRegistration(scratch.Key, @"D:\elsewhere\aesx.exe").Register());
        Assert.Equal("\"D:\\elsewhere\\aesx.exe\"", Value($@"CLSID\{Class}\LocalServer32", null));

        new ShellRegistration(scratch.Key, "").Unregister();
        Assert.Empty(scratch.Key.GetSubKeyNames());
        new ShellRegistration(scratch.Key, "").Unregister(); // and once more is harmless
    }

    [Fact]
    public void WhatBelongsToOtherProgramsIsLeftAlone()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var scratch = new ScratchKey();
        using (RegistryKey extension = scratch.Key.CreateSubKey(".aesx"))
        {
            extension.SetValue(null, "Other.Program");
            using RegistryKey openWith = extension.CreateSubKey("OpenWithProgids");
            openWith.SetValue("Other.Program", "");
        }
        using (RegistryKey verb = scratch.Key.CreateSubKey(@"*\shell\SomebodyElse"))
        {
            verb.SetValue(null, "Their entry");
        }
        using (RegistryKey other = scratch.Key.CreateSubKey(@"CLSID\{00000000-1111-2222-3333-444444444444}"))
        {
            other.SetValue(null, "Their class");
        }

        string? owner = new ShellRegistration(scratch.Key, Executable).Register();
        new ShellRegistration(scratch.Key, "").Unregister();

        Assert.Equal("Other.Program", owner);
        using RegistryKey? left = scratch.Key.OpenSubKey(".aesx");
        Assert.Equal("Other.Program", left?.GetValue(null));
        using RegistryKey? stillOpenWith = scratch.Key.OpenSubKey(@".aesx\OpenWithProgids");
        Assert.NotNull(stillOpenWith);
        Assert.Equal(["Other.Program"], stillOpenWith.GetValueNames());
        using RegistryKey? theirs = scratch.Key.OpenSubKey(@"*\shell\SomebodyElse");
        Assert.Equal("Their entry", theirs?.GetValue(null));
        Assert.Equal(["*", ".aesx", "CLSID"], scratch.Key.GetSubKeyNames().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RegisterNeedsWindowsAndTakesNoArguments()
    {
        CliResult extra = await Cli.RunAsync(null, "register", "something");
        Assert.Equal(2, extra.ExitCode);
        Assert.Contains("takes no arguments", extra.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, (await Cli.RunAsync(null, "unregister", "--force")).ExitCode);

        if (OperatingSystem.IsWindows()) return; // on Windows a plain 'register' would change the menus of whoever runs the tests
        CliResult run = await Cli.RunAsync(null, "register");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Windows Explorer", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASelectionMadeInTheShellIsEncryptedByOneProcessAskingOnce()
    {
        if (!OperatingSystem.IsWindows() || Shell.IsRegisteredForReal()) return;
        using var dir = new TempDir();
        using var extension = new ScratchExtension();
        string[] files = [dir.File("one" + extension.Name), dir.File("two" + extension.Name), dir.File("три 🔐" + extension.Name)];
        foreach (string file in files) await File.WriteAllTextAsync(file, "contents of " + Path.GetFileName(file));

        using Process aesx = Process.Start(Cli.StartInfo(new CliRun(), "-Embedding"))!;
        Task<string> errors = aesx.StandardError.ReadToEndAsync();
        Task<string> output = aesx.StandardOutput.ReadToEndAsync();
        try
        {
            // The same call Explorer makes when "Encrypt with aesx" is chosen for a selection. It is
            // refused until aesx has announced itself, a moment after starting.
            Shell.InvokeUntilAccepted(files, ShellRegistration.EncryptVerb, Patience);

            nint window = Shell.WaitForWindow(aesx.Id, withPasswordField: true, Patience);
            Shell.Type(window, PasswordDialog.PasswordField, Pw);
            Shell.Type(window, PasswordDialog.ConfirmField, Pw);
            Shell.Press(window, PasswordDialog.OkButton);

            await aesx.WaitForExitAsync(new CancellationTokenSource(Patience).Token);
            Assert.True(aesx.ExitCode == 0, $"exit {aesx.ExitCode}\n{await output}\n{await errors}");
        }
        finally
        {
            if (!aesx.HasExited) aesx.Kill();
        }

        foreach (string file in files)
        {
            CliResult dec = await Cli.RunAsync(null, "decrypt", file + ".aesx", "-p", Pw, "-o", "-", "-q");
            Assert.True(dec.ExitCode == 0, dec.ToString());
            Assert.Equal("contents of " + Path.GetFileName(file), dec.Stdout);
        }
    }

    [Fact]
    public async Task FilesAndFoldersHandedOverTogetherAreAllDealtWith()
    {
        if (!OperatingSystem.IsWindows() || Shell.IsRegisteredForReal()) return;
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("notes.txt"), "notes");
        Directory.CreateDirectory(dir.File("photos"));
        await File.WriteAllTextAsync(dir.File("photos/a.txt"), "a");

        using Process aesx = Process.Start(Cli.StartInfo(new CliRun(), "-Embedding"))!;
        Task<string> errors = aesx.StandardError.ReadToEndAsync();
        Task<string> output = aesx.StandardOutput.ReadToEndAsync();
        try
        {
            Shell.HandOver([dir.File("notes.txt"), dir.File("photos")], ShellRegistration.EncryptVerb, Patience);
            nint window = Shell.WaitForWindow(aesx.Id, withPasswordField: true, Patience);
            Shell.Type(window, PasswordDialog.PasswordField, Pw);
            Shell.Type(window, PasswordDialog.ConfirmField, Pw);
            Shell.Press(window, PasswordDialog.OkButton);
            await aesx.WaitForExitAsync(new CancellationTokenSource(Patience).Token);
            Assert.True(aesx.ExitCode == 0, $"exit {aesx.ExitCode}\n{await output}\n{await errors}");
        }
        finally
        {
            if (!aesx.HasExited) aesx.Kill();
        }

        Assert.Equal(["notes.txt", "notes.txt.aesx", "photos", "photos.folder.aesx"], Directory.GetFileSystemEntries(dir.Path).Select(e => Path.GetFileName(e)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AWrongPasswordGivenThroughTheMenuIsReportedInAWindow()
    {
        if (!OperatingSystem.IsWindows() || Shell.IsRegisteredForReal()) return;
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("secret.txt"), "secret");
        Assert.Equal(0, (await Cli.RunAsync(null, "encrypt", dir.File("secret.txt"), "-p", Pw, "--delete", "--iterations", "1000")).ExitCode);

        using Process aesx = Process.Start(Cli.StartInfo(new CliRun(), "-Embedding"))!;
        Task<string> errors = aesx.StandardError.ReadToEndAsync();
        _ = aesx.StandardOutput.ReadToEndAsync();
        try
        {
            Shell.HandOver([dir.File("secret.txt.aesx")], ShellRegistration.DecryptVerb, Patience);
            nint window = Shell.WaitForWindow(aesx.Id, withPasswordField: true, Patience);
            Assert.Equal(0, Shell.Control(window, PasswordDialog.ConfirmField)); // decrypting asks once
            Shell.Type(window, PasswordDialog.PasswordField, "not the password");
            Shell.Press(window, PasswordDialog.OkButton);

            nint report = Shell.WaitForMessage(aesx.Id, "wrong password", Patience);
            Shell.Close(report);
            await aesx.WaitForExitAsync(new CancellationTokenSource(Patience).Token);
            Assert.Equal(1, aesx.ExitCode);
            Assert.Contains("wrong password", await errors, StringComparison.Ordinal);
        }
        finally
        {
            if (!aesx.HasExited) aesx.Kill();
        }
        Assert.Equal(["secret.txt.aesx"], dir.Files());
    }

    [Fact]
    public async Task ClosingThePasswordWindowDoesNothingAtAll()
    {
        if (!OperatingSystem.IsWindows() || Shell.IsRegisteredForReal()) return;
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("untouched.txt"), "x");

        using Process aesx = Process.Start(Cli.StartInfo(new CliRun(), "-Embedding"))!;
        _ = aesx.StandardError.ReadToEndAsync();
        _ = aesx.StandardOutput.ReadToEndAsync();
        try
        {
            Shell.HandOver([dir.File("untouched.txt")], ShellRegistration.EncryptVerb, Patience);
            nint window = Shell.WaitForWindow(aesx.Id, withPasswordField: true, Patience);
            Shell.Type(window, PasswordDialog.PasswordField, Pw);
            Shell.Type(window, PasswordDialog.ConfirmField, Pw + " with a typo");
            Shell.Press(window, PasswordDialog.OkButton);

            // The two do not match: a message says so, and the window stays for another try.
            nint complaint = Shell.WaitForMessage(aesx.Id, "not the same", Patience);
            Shell.Close(complaint);
            Shell.WaitUntilGone(complaint, Patience);

            Shell.Close(window);
            await aesx.WaitForExitAsync(new CancellationTokenSource(Patience).Token);
            Assert.Equal(130, aesx.ExitCode);
        }
        finally
        {
            if (!aesx.HasExited) aesx.Kill();
        }
        Assert.Equal(["untouched.txt"], dir.Files());
    }

    [Fact]
    public async Task WindowsStartsAesxItselfWhenAMenuEntryIsChosen()
    {
        if (!OperatingSystem.IsWindows() || Shell.IsRegisteredForReal()) return;
        using var dir = new TempDir();
        using var extension = new ScratchExtension();
        string file = dir.File("started by windows" + extension.Name);
        await File.WriteAllTextAsync(file, "launched");

        // The class entry that 'aesx register' writes, pointing at the build under test, for the
        // length of this test.
        using var registered = new ScratchClassRegistration(Cli.StartInfo(new CliRun()).FileName);
        int verdict = Shell.Invoke([file], ShellRegistration.EncryptVerb);
        if (verdict != 0)
        {
            // Where the registry of the test process is private to it (inside a packaged
            // application, for one), Windows cannot see the entry and there is nothing to test.
            // On the CI runners the registry is the real one, and there this has to work.
            Assert.False(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", $"the shell refused the menu entry: 0x{verdict:X8}");
            return;
        }

        nint window = Shell.WaitForWindow(processId: null, withPasswordField: true, Patience);
        using Process aesx = Process.GetProcessById(Shell.ProcessOf(window));
        try
        {
            Shell.Type(window, PasswordDialog.PasswordField, Pw);
            Shell.Type(window, PasswordDialog.ConfirmField, Pw);
            Shell.Press(window, PasswordDialog.OkButton);
            await aesx.WaitForExitAsync(new CancellationTokenSource(Patience).Token);
        }
        finally
        {
            if (!aesx.HasExited) aesx.Kill();
        }

        CliResult dec = await Cli.RunAsync(null, "decrypt", file + ".aesx", "-p", Pw, "-o", "-", "-q");
        Assert.True(dec.ExitCode == 0, dec.ToString());
        Assert.Equal("launched", dec.Stdout);
    }

    /// <summary>A registry key of our own that disappears with the test.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class ScratchKey : IDisposable
    {
        private readonly string _path = $@"Software\aesx-tests\{Guid.NewGuid():N}";

        public ScratchKey() => Key = Registry.CurrentUser.CreateSubKey(_path);

        public RegistryKey Key { get; }

        public void Dispose()
        {
            Key.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
            using RegistryKey? parent = Registry.CurrentUser.OpenSubKey(@"Software\aesx-tests");
            if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\aesx-tests", throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// A file extension nobody else has, carrying the same two menu entries that registering
    /// writes for every file and for .aesx files, so the shell can be asked to invoke them.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class ScratchExtension : IDisposable
    {
        public ScratchExtension()
        {
            Name = ".aesxtest" + Guid.NewGuid().ToString("N")[..8];
            using RegistryKey shell = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Name}\shell");
            ShellRegistration.WriteVerb(shell, ShellRegistration.EncryptVerb, "Encrypt with aesx", "encrypt", Executable);
        }

        public string Name { get; }

        public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{Name}", throwOnMissingSubKey: false);
    }

    /// <summary>The class entry of a real registration, present only while a test runs.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class ScratchClassRegistration : IDisposable
    {
        private const string KeyPath = $@"Software\Classes\CLSID\{Class}";

        public ScratchClassRegistration(string executable)
        {
            using RegistryKey server = Registry.CurrentUser.CreateSubKey(KeyPath + @"\LocalServer32");
            server.SetValue(null, $"\"{executable}\"");
        }

        public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
    }

    /// <summary>Plays Explorer: asks the shell to invoke a menu entry, and operates the windows aesx then shows.</summary>
    [SupportedOSPlatform("windows")]
    private static class Shell
    {
        private const uint LocalServer = 0x4;
        private const uint WmSetText = 0x000C;
        private const uint WmClose = 0x0010;
        private const uint WmCommand = 0x0111;
        private static readonly Guid s_unknown = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid s_shellFolder = new("000214E6-0000-0000-C000-000000000046");
        private static readonly Guid s_contextMenu = new("000214E4-0000-0000-C000-000000000046");

        /// <summary>A developer who uses aesx has the real entries, and a test must not point them at a test build.</summary>
        public static bool IsRegisteredForReal()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{Class}");
            return key is not null;
        }

        /// <summary>Invokes a menu entry for a selection the way Explorer does; returns the shell's verdict.</summary>
        public static int Invoke(string[] paths, string verb)
        {
            nint[] absolute = new nint[paths.Length];
            nint[] children = new nint[paths.Length];
            nint verbName = Marshal.StringToHGlobalAnsi(verb);
            nint popup = CreatePopupMenu();
            try
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    Marshal.ThrowExceptionForHR(SHParseDisplayName(paths[i], 0, out absolute[i], 0, out _));
                    children[i] = ILFindLastID(absolute[i]);
                }
                Marshal.ThrowExceptionForHR(SHBindToParent(absolute[0], in s_shellFolder, out nint folderPointer, out _));
                var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPointer);
                Marshal.Release(folderPointer);
                folder.GetUIObjectOf(0, (uint)children.Length, children, in s_contextMenu, 0, out object menuObject);
                var menu = (IContextMenu)menuObject;
                _ = menu.QueryContextMenu(popup, 0, 1, 0x7FFF, 0);

                var command = new InvokeCommandInfo
                {
                    Size = Marshal.SizeOf<InvokeCommandInfo>(),
                    Mask = 0x400, // no message boxes from the shell itself
                    Verb = verbName,
                    Show = 1,
                };
                return menu.InvokeCommand(ref command);
            }
            finally
            {
                DestroyMenu(popup);
                Marshal.FreeHGlobal(verbName);
                foreach (nint item in absolute) Marshal.FreeCoTaskMem(item);
            }
        }

        public static void InvokeUntilAccepted(string[] paths, string verb, TimeSpan patience)
        {
            long started = Stopwatch.GetTimestamp();
            int result;
            while ((result = Invoke(paths, verb)) != 0)
            {
                if (Stopwatch.GetElapsedTime(started) > patience) Marshal.ThrowExceptionForHR(result);
                Thread.Sleep(100);
            }
        }

        /// <summary>
        /// Hands a selection to a waiting aesx directly, the way the shell does after it has
        /// worked out which program a menu entry belongs to. Needs no registry entry at all.
        /// </summary>
        public static void HandOver(string[] paths, string verb, TimeSpan patience)
        {
            long started = Stopwatch.GetTimestamp();
            nint instance;
            int result;
            while ((result = CoCreateInstance(in ExplorerSelection.ClassId, 0, LocalServer, in s_unknown, out instance)) != 0)
            {
                if (Stopwatch.GetElapsedTime(started) > patience) Marshal.ThrowExceptionForHR(result);
                Thread.Sleep(100);
            }

            nint[] items = new nint[paths.Length];
            try
            {
                for (int i = 0; i < paths.Length; i++) Marshal.ThrowExceptionForHR(SHParseDisplayName(paths[i], 0, out items[i], 0, out _));
                Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)items.Length, items, out nint array));

                var wrappers = new StrategyBasedComWrappers();
                object command = wrappers.GetOrCreateObjectForComInstance(instance, CreateObjectFlags.None);
                var selection = (IShellItemArray)wrappers.GetOrCreateObjectForComInstance(array, CreateObjectFlags.None);
                Marshal.Release(array);
                ((IInitializeCommand)command).Initialize(verb, 0);
                ((IObjectWithSelection)command).SetSelection(selection);
                ((IExecuteCommand)command).Execute();
            }
            finally
            {
                Marshal.Release(instance);
                foreach (nint item in items) Marshal.FreeCoTaskMem(item);
            }
        }

        /// <summary>
        /// Waits for one of the two windows aesx shows: the password dialog, or a message box. Both
        /// are dialogs titled "aesx"; only the first has a password field.
        /// </summary>
        public static nint WaitForWindow(int? processId, bool withPasswordField, TimeSpan patience)
        {
            long started = Stopwatch.GetTimestamp();
            while (true)
            {
                nint found = 0;
                EnumWindows(
                    (window, unused) =>
                    {
                        _ = GetWindowThreadProcessId(window, out uint owner);
                        if (processId is { } wanted && owner != wanted) return true;
                        if (ClassOf(window) != "#32770" || TextOf(window) != PasswordDialog.Title) return true;
                        if (Control(window, PasswordDialog.PasswordField) != 0 != withPasswordField) return true;
                        found = window;
                        return false;
                    },
                    0);
                if (found != 0) return found;
                if (Stopwatch.GetElapsedTime(started) > patience)
                    throw new TimeoutException($"aesx did not show its {(withPasswordField ? "password window" : "message")} within {patience.TotalSeconds:F0} s");
                Thread.Sleep(50);
            }
        }

        /// <summary>
        /// Waits for a message box of aesx that says <paramref name="expected"/>. Looking for "a
        /// dialog without a password field" is not enough: a password window that is just being
        /// torn down has lost its fields too, and a message box that is just being built has no
        /// text yet. So this keeps looking until the words are there.
        /// </summary>
        public static nint WaitForMessage(int processId, string expected, TimeSpan patience)
        {
            long started = Stopwatch.GetTimestamp();
            string seen = "";
            while (true)
            {
                nint found = 0;
                EnumWindows(
                    (window, unused) =>
                    {
                        _ = GetWindowThreadProcessId(window, out uint owner);
                        if (owner != processId || ClassOf(window) != "#32770" || TextOf(window) != PasswordDialog.Title) return true;
                        if (Control(window, PasswordDialog.PasswordField) != 0) return true;
                        string text = TextIn(window);
                        if (text.Length > 0) seen = text;
                        if (!text.Contains(expected, StringComparison.Ordinal)) return true;
                        found = window;
                        return false;
                    },
                    0);
                if (found != 0) return found;
                if (Stopwatch.GetElapsedTime(started) > patience)
                    throw new TimeoutException($"aesx did not show a message saying '{expected}' within {patience.TotalSeconds:F0} s; the last message seen was: '{seen.Trim()}'");
                Thread.Sleep(50);
            }
        }

        public static void WaitUntilGone(nint window, TimeSpan patience)
        {
            long started = Stopwatch.GetTimestamp();
            while (IsWindow(window))
            {
                if (Stopwatch.GetElapsedTime(started) > patience) throw new TimeoutException("a window of aesx did not close");
                Thread.Sleep(50);
            }
        }

        public static int ProcessOf(nint window)
        {
            _ = GetWindowThreadProcessId(window, out uint owner);
            return (int)owner;
        }

        public static nint Control(nint window, int id) => GetDlgItem(window, id);

        public static void Type(nint window, int field, string text) => SendMessageW(GetDlgItem(window, field), WmSetText, 0, text);

        public static void Press(nint window, int button) => PostMessageW(window, WmCommand, (nuint)button, 0);

        public static void Close(nint window) => PostMessageW(window, WmClose, 0, 0);

        /// <summary>Everything written in a window's own controls, which for a message box is the message.</summary>
        public static string TextIn(nint window)
        {
            var text = new StringBuilder();
            EnumChildWindows(
                window,
                (child, unused) =>
                {
                    text.AppendLine(TextOf(child));
                    return true;
                },
                0);
            return text.ToString();
        }

        private static string ClassOf(nint window)
        {
            char[] buffer = new char[64];
            return new string(buffer, 0, GetClassNameW(window, buffer, buffer.Length));
        }

        private static string TextOf(nint window)
        {
            char[] buffer = new char[2048];
            return new string(buffer, 0, GetWindowTextW(window, buffer, buffer.Length));
        }

        private delegate bool WindowVisitor(nint window, nint parameter);

        [StructLayout(LayoutKind.Sequential)]
        private struct InvokeCommandInfo
        {
            public int Size;
            public int Mask;
            public nint Window;
            public nint Verb;
            public nint Parameters;
            public nint Directory;
            public int Show;
            public int HotKey;
            public nint Icon;
        }

        [ComImport]
        [Guid("000214E6-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellFolder
        {
            void ParseDisplayName(nint window, nint bindContext, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint eaten, out nint item, ref uint attributes);
            void EnumObjects(nint window, int flags, out nint enumerator);
            void BindToObject(nint item, nint bindContext, in Guid interfaceId, out nint result);
            void BindToStorage(nint item, nint bindContext, in Guid interfaceId, out nint result);

            [PreserveSig]
            int CompareIDs(nint parameter, nint first, nint second);

            void CreateViewObject(nint owner, in Guid interfaceId, out nint result);
            void GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray)] nint[] items, ref uint attributes);
            void GetUIObjectOf(nint owner, uint count, [MarshalAs(UnmanagedType.LPArray)] nint[] items, in Guid interfaceId, nint reserved, [MarshalAs(UnmanagedType.Interface)] out object result);
        }

        [ComImport]
        [Guid("000214E4-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu
        {
            [PreserveSig]
            int QueryContextMenu(nint menu, uint index, uint firstId, uint lastId, uint flags);

            [PreserveSig]
            int InvokeCommand(ref InvokeCommandInfo command);

            [PreserveSig]
            int GetCommandString(nuint id, uint type, nint reserved, nint name, uint capacity);
        }

        [DllImport("ole32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, out nint instance);

        [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int SHParseDisplayName(string name, nint bindContext, out nint item, uint attributesIn, out uint attributesOut);

        [DllImport("shell32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int SHCreateShellItemArrayFromIDLists(uint count, nint[] items, out nint array);

        [DllImport("shell32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int SHBindToParent(nint item, in Guid interfaceId, out nint parent, out nint last);

        [DllImport("shell32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint ILFindLastID(nint item);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint CreatePopupMenu();

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyMenu(nint menu);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(WindowVisitor visitor, nint parameter);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(nint parent, WindowVisitor visitor, nint parameter);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int GetClassNameW(nint window, [Out] char[] name, int capacity);

        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int GetWindowTextW(nint window, [Out] char[] text, int capacity);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(nint window);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint GetDlgItem(nint window, int id);

        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint SendMessageW(nint window, uint message, nuint wParam, string lParam);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
    }
}
