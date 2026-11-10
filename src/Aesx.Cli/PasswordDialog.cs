// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Aesx.Cli;

/// <summary>
/// The password window shown when Windows Explorer started aesx (a menu entry, a double-click,
/// files dropped onto the executable): a plain Win32 dialog with a password field, a second one
/// to confirm when encrypting, and a box to show what was typed. Somebody who did not open a
/// terminal should not have to type into one blind.
/// </summary>
/// <remarks>
/// The dialog is described in memory and run by the operating system's dialog manager; there is
/// no UI framework behind it. The characters typed live in the edit control until OK is pressed
/// and are then copied into an array the caller wipes; the control is overwritten and emptied,
/// which is as much as a window allows.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class PasswordDialog
{
    public const string Title = "aesx";
    public const int PasswordField = 101;
    public const int ConfirmField = 102;
    public const int ShowBox = 103;
    public const int OkButton = 1;
    public const int CancelButton = 2;

    private const int MaxLength = 4096;

    private const uint WmInitDialog = 0x0110;
    private const uint WmCommand = 0x0111;
    private const uint EmSetLimitText = 0x00C5;
    private const uint EmGetPasswordChar = 0x00D2;
    private const uint EmSetPasswordChar = 0x00CC;

    // Window and control styles.
    private const uint DialogStyle = 0x80000000 | 0x00C00000 | 0x00080000 | 0x80 | 0x40 | 0x0800 | 0x0200; // popup, caption, system menu; modal frame, set font, centred, foreground
    private const uint Child = 0x40000000 | 0x10000000;       // WS_CHILD | WS_VISIBLE
    private const uint TabStop = 0x00010000;
    private const uint Border = 0x00800000;
    private const uint TextNoPrefixEllipsis = 0x80 | 0x4000;  // SS_NOPREFIX | SS_ENDELLIPSIS: a file name is text, not an accelerator
    private const uint EditPassword = 0x20 | 0x80;            // ES_PASSWORD | ES_AUTOHSCROLL
    private const uint CheckBox = 0x3;                        // BS_AUTOCHECKBOX
    private const uint DefaultButton = 0x1;                   // BS_DEFPUSHBUTTON

    private const ushort ButtonClass = 0x0080;
    private const ushort EditClass = 0x0081;
    private const ushort StaticClass = 0x0082;

    // One dialog at a time, on the thread that asked: the procedure reaches its state here.
    private static readonly DialogProcedure s_procedure = Procedure;
    private static bool s_confirm;
    private static nint s_maskCharacter;
    private static char[]? s_password;

    private delegate nint DialogProcedure(nint window, uint message, nuint wParam, nint lParam);

    /// <summary>Shows the window and waits for it.</summary>
    /// <param name="purpose">What the password is for, shown above the fields ("Encrypt report.pdf").</param>
    /// <param name="confirm">Ask for the password twice.</param>
    /// <returns>The characters typed, for the caller to wipe; or null if no window could be shown.</returns>
    /// <exception cref="PromptCancelledException">The window was closed without a password.</exception>
    public static char[]? Ask(string purpose, bool confirm)
    {
        s_confirm = confirm;
        s_password = null;
        byte[] template = BuildTemplate(purpose, confirm);

        nint outcome;
        using (new VisualStyles())
        {
            outcome = DialogBoxIndirectParamW(GetModuleHandleW(0), template, 0, Marshal.GetFunctionPointerForDelegate(s_procedure), 0);
        }
        if (outcome == OkButton && s_password is { } typed)
        {
            s_password = null;
            return typed;
        }
        if (outcome is 0 or CancelButton) throw new PromptCancelledException();
        return null;
    }

    private static nint Procedure(nint window, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case WmInitDialog:
                SendDlgItemMessageW(window, PasswordField, EmSetLimitText, MaxLength, 0);
                SendDlgItemMessageW(window, ConfirmField, EmSetLimitText, MaxLength, 0);
                s_maskCharacter = SendDlgItemMessageW(window, PasswordField, EmGetPasswordChar, 0, 0);
                return 1; // the dialog manager puts the focus on the first field

            case WmCommand:
                switch ((int)(wParam & 0xFFFF))
                {
                    case OkButton:
                        if (Accept(window)) EndDialog(window, OkButton);
                        return 1;
                    case CancelButton:
                        Clear(window);
                        EndDialog(window, CancelButton);
                        return 1;
                    case ShowBox:
                        nuint mask = IsDlgButtonChecked(window, ShowBox) == 1 ? 0 : (nuint)s_maskCharacter;
                        foreach (int field in (ReadOnlySpan<int>)[PasswordField, ConfirmField])
                        {
                            nint control = GetDlgItem(window, field);
                            if (control == 0) continue; // no confirmation field when decrypting
                            SendDlgItemMessageW(window, field, EmSetPasswordChar, mask, 0);
                            InvalidateRect(control, 0, erase: true);
                        }
                        return 1;
                    default:
                        break;
                }
                break;

            default:
                break;
        }
        return 0;
    }

    /// <summary>Takes the password out of the window if it is usable; says why if it is not.</summary>
    private static bool Accept(nint window)
    {
        char[] password = Read(window, PasswordField);
        if (password.Length == 0)
        {
            Complain(window, "The password must not be empty.", PasswordField);
            return false;
        }

        if (s_confirm)
        {
            char[] again = Read(window, ConfirmField);
            bool same = CryptographicOperations.FixedTimeEquals(MemoryMarshal.AsBytes(password.AsSpan()), MemoryMarshal.AsBytes(again.AsSpan()));
            Array.Clear(again);
            if (!same)
            {
                Array.Clear(password);
                Complain(window, "The two passwords are not the same.", ConfirmField);
                return false;
            }
        }

        s_password = password;
        Clear(window);
        return true;
    }

    private static char[] Read(nint window, int field)
    {
        nint control = GetDlgItem(window, field);
        if (control == 0) return []; // the confirmation field of a dialog that has none
        char[] buffer = new char[GetWindowTextLengthW(control) + 1];
        try
        {
            int length = GetWindowTextW(control, ref buffer[0], buffer.Length);
            return buffer.AsSpan(0, Math.Clamp(length, 0, buffer.Length - 1)).ToArray();
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    /// <summary>Writes over what the fields hold before emptying them.</summary>
    private static void Clear(nint window)
    {
        foreach (int field in (ReadOnlySpan<int>)[PasswordField, ConfirmField])
        {
            nint control = GetDlgItem(window, field);
            if (control == 0) continue;
            SetWindowTextW(control, new string(' ', GetWindowTextLengthW(control)));
            SetWindowTextW(control, "");
        }
    }

    /// <summary>Shows what a run started from an Explorer menu has to report, in a message box of its own.</summary>
    public static void Tell(IReadOnlyList<string> lines, bool failed)
    {
        const int most = 15;
        var text = new StringBuilder();
        foreach (string line in lines.Take(most)) text.AppendLine(line);
        if (lines.Count > most) text.AppendLine($"... and {lines.Count - most} more.");
        using (new VisualStyles())
        {
            // An OK button; an error or a warning icon; asking to come to the front.
            MessageBoxW(0, text.ToString().TrimEnd(), Title, (failed ? 0x10u : 0x30u) | 0x10000u);
        }
    }

    private static void Complain(nint window, string message, int focus)
    {
        MessageBoxW(window, message, Title, 0x30); // OK button, warning icon
        SetFocus(GetDlgItem(window, focus));
    }

    /// <summary>
    /// The dialog as the dialog manager wants it: a DLGTEMPLATE followed by one DLGITEMTEMPLATE
    /// per control, each aligned to four bytes. Coordinates are dialog units, which scale with
    /// the font and the display.
    /// </summary>
    internal static byte[] BuildTemplate(string purpose, bool confirm)
    {
        const short width = 230, margin = 7, labelWidth = 60, fieldLeft = 70, rowHeight = 18;
        short fieldWidth = (short)(width - fieldLeft - margin);
        short showTop = (short)(confirm ? 22 + 2 * rowHeight : 22 + rowHeight);
        short buttonTop = (short)(showTop + 16);
        short height = (short)(buttonTop + 14 + margin);

        var template = new MemoryStream();
        void Word(int value)
        {
            Span<byte> two = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(two, (ushort)value);
            template.Write(two);
        }
        void DoubleWord(uint value)
        {
            Span<byte> four = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(four, value);
            template.Write(four);
        }
        void Text(string text)
        {
            template.Write(Encoding.Unicode.GetBytes(text));
            Word(0);
        }
        void Control(ushort windowClass, uint style, short x, short y, short w, short h, int id, string text)
        {
            while (template.Length % 4 != 0) template.WriteByte(0);
            DoubleWord(Child | style);
            DoubleWord(0);
            Word(x);
            Word(y);
            Word(w);
            Word(h);
            Word(id);
            Word(0xFFFF);
            Word(windowClass);
            Text(text);
            Word(0); // no creation data
        }

        int controls = confirm ? 8 : 6;
        DoubleWord(DialogStyle);
        DoubleWord(0);
        Word(controls);
        Word(0);
        Word(0);
        Word(width);
        Word(height);
        Word(0);          // no menu
        Word(0);          // the standard dialog class
        Text(Title);
        Word(9);
        Text("Segoe UI");

        Control(StaticClass, TextNoPrefixEllipsis, margin, margin, (short)(width - 2 * margin), 9, -1, purpose);
        Control(StaticClass, 0, margin, 24, labelWidth, 9, -1, "&Password:");
        Control(EditClass, EditPassword | Border | TabStop, fieldLeft, 22, fieldWidth, 13, PasswordField, "");
        if (confirm)
        {
            Control(StaticClass, 0, margin, (short)(24 + rowHeight), labelWidth, 9, -1, "&Once more:");
            Control(EditClass, EditPassword | Border | TabStop, fieldLeft, (short)(22 + rowHeight), fieldWidth, 13, ConfirmField, "");
        }
        Control(ButtonClass, CheckBox | TabStop, fieldLeft, showTop, fieldWidth, 10, ShowBox, "&Show the password");
        Control(ButtonClass, DefaultButton | TabStop, (short)(width - margin - 104), buttonTop, 50, 14, OkButton, "OK");
        Control(ButtonClass, TabStop, (short)(width - margin - 50), buttonTop, 50, 14, CancelButton, "Cancel");
        return template.ToArray();
    }

    /// <summary>
    /// Gives the dialog today's look. Without an application manifest a program gets the
    /// controls of twenty years ago and a blurry window on a scaled display; both are asked for
    /// here, for the life of the dialog only, and neither is required for it to work.
    /// </summary>
    private sealed class VisualStyles : IDisposable
    {
        private const uint ResourceNameValid = 0x008;
        private const uint AssemblyDirectoryValid = 0x004;
        private const nint ShellManifest = 124;                // the manifest shell32.dll carries for exactly this purpose
        private static readonly nint s_perMonitorAware = -4;   // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2

        private readonly nint _context = -1;
        private readonly nuint _cookie;
        private readonly bool _activated;
        private readonly nint _previousAwareness;

        public VisualStyles()
        {
            string system = Environment.SystemDirectory;
            nint source = Marshal.StringToHGlobalUni(Path.Combine(system, "shell32.dll"));
            nint directory = Marshal.StringToHGlobalUni(system);
            try
            {
                var request = new ActivationContext
                {
                    Size = (uint)Marshal.SizeOf<ActivationContext>(),
                    Flags = ResourceNameValid | AssemblyDirectoryValid,
                    Source = source,
                    AssemblyDirectory = directory,
                    ResourceName = ShellManifest,
                };
                _context = CreateActCtxW(ref request);
                _activated = _context != -1 && ActivateActCtx(_context, out _cookie);
            }
            finally
            {
                Marshal.FreeHGlobal(source);
                Marshal.FreeHGlobal(directory);
            }

            try
            {
                _previousAwareness = SetThreadDpiAwarenessContext(s_perMonitorAware);
            }
            catch (EntryPointNotFoundException)
            {
                // Windows 10 before version 1607: the dialog is merely scaled by the system.
            }
        }

        public void Dispose()
        {
            if (_previousAwareness != 0) SetThreadDpiAwarenessContext(_previousAwareness);
            if (_activated) DeactivateActCtx(0, _cookie);
            if (_context != -1) ReleaseActCtx(_context);
        }
    }

    /// <summary>ACTCTXW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationContext
    {
        public uint Size;
        public uint Flags;
        public nint Source;
        public ushort ProcessorArchitecture;
        public ushort LanguageId;
        public nint AssemblyDirectory;
        public nint ResourceName;
        public nint ApplicationName;
        public nint Module;
    }

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint DialogBoxIndirectParamW(nint instance, byte[] template, nint parent, nint procedure, nint parameter);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EndDialog(nint window, nint result);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetDlgItem(nint window, int id);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint SendDlgItemMessageW(nint window, int id, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint IsDlgButtonChecked(nint window, int id);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InvalidateRect(nint window, nint rectangle, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint SetFocus(nint window);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetWindowTextLengthW(nint window);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetWindowTextW(nint window, ref char text, int capacity);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowTextW(nint window, string text);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int MessageBoxW(nint window, string text, string caption, uint type);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetModuleHandleW(nint moduleName);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateActCtxW(ref ActivationContext request);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ActivateActCtx(nint context, out nuint cookie);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeactivateActCtx(uint flags, nuint cookie);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void ReleaseActCtx(nint context);
}
