// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Aesx.Cli;

/// <summary><c>aesx register</c> and <c>aesx unregister</c> on macOS: the Finder integration for the current user.</summary>
internal static class FinderIntegration
{
    private const string LaunchServices = "/System/Library/Frameworks/CoreServices.framework/Versions/A/Frameworks/LaunchServices.framework/Versions/A/Support/lsregister";
    private const string Pasteboard = "/System/Library/CoreServices/pbs";

    public static int Run(Options options, TextWriter status)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length == 0) throw new InvalidOperationException("cannot tell where your home folder is");
        string services = Path.Combine(home, "Library", "Services");
        string applications = Path.Combine(home, "Applications");

        if (options.Command == Command.Unregister)
        {
            var existing = new FinderRegistration(services, applications, executable: "");

            // Launch Services is told while the application is still there to be looked at.
            if (Directory.Exists(existing.Application)) MacTool.Try(LaunchServices, "-u", existing.Application);
            IReadOnlyList<string> foreign = existing.Unregister();
            MacTool.Try(Pasteboard, "-update");
            status.WriteLine("Removed aesx from the Finder menus and from .aesx files.");
            foreach (string path in foreign)
                Console.Error.WriteLine($"note: {TextSafety.Display(path)} was not written by aesx and was left alone");
            return Program.ExitSuccess;
        }

        string running = Environment.ProcessPath ?? "";
        if (!string.Equals(Path.GetFileName(running), "aesx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("'register' has to be run by aesx itself, because the menu entries point at it");
        if (IsTemporary(running))
            throw new InvalidOperationException($"aesx is running from a temporary folder ({TextSafety.Display(Path.GetDirectoryName(running) ?? "")}); put it somewhere permanent and run 'aesx register' there");
        string executable = StableExecutablePath(running, Environment.GetEnvironmentVariable("PATH"));

        var registration = new FinderRegistration(services, applications, executable);
        registration.Register();

        // Both would notice by themselves sooner or later; this makes it now.
        MacTool.Try(LaunchServices, "-f", registration.Application);
        MacTool.Try(Pasteboard, "-update");

        status.WriteLine($"""
            Registered for the current user (nothing outside your home folder is touched):
              right-click files or folders   {FinderRegistration.EncryptEntry}
              right-click .aesx files        {FinderRegistration.DecryptEntry}
              double-click an .aesx file     decrypts it
              drop files or folders          onto aesx in the Applications folder of your home folder
            The two entries are at the bottom of the right-click menu, under Services if there
            are many. Any number of items can be selected; the password is asked once.
            They point at {TextSafety.Display(executable)}
            Run 'aesx register' again after moving it, and 'aesx unregister' to remove everything.
            """);
        return Program.ExitSuccess;
    }

    /// <summary>
    /// Where the entries should point. A package manager keeps every version in a folder of its
    /// own and puts a link on the PATH that it moves with each upgrade (Homebrew does). The
    /// entries have to name that link: the folder behind it is gone after the next upgrade.
    /// </summary>
    /// <param name="running">The executable of this process, links resolved.</param>
    /// <param name="searchPath">The PATH variable.</param>
    internal static string StableExecutablePath(string running, string? searchPath)
    {
        foreach (string directory in (searchPath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, Path.GetFileName(running));
            if (!Path.IsPathFullyQualified(candidate)) continue;
            try
            {
                if (File.ResolveLinkTarget(candidate, returnFinalTarget: true) is { } target
                    && string.Equals(target.FullName, running, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing by that name in this directory, or nothing we may look at.
            }
        }
        return running;
    }

    /// <summary>True for a path below the temporary folder of this account. macOS reaches that folder under two names, <c>/var</c> and <c>/private/var</c>.</summary>
    internal static bool IsTemporary(string path)
    {
        static string Canonical(string p) => p.StartsWith("/private/", StringComparison.Ordinal) ? p["/private".Length..] : p;
        string temp = Canonical(Path.GetTempPath());
        return temp.Length > 1 && Canonical(path).StartsWith(temp, StringComparison.Ordinal);
    }
}

/// <summary>
/// What registering writes on a Mac, all of it in the home folder: two services for the Finder's
/// right-click menu ("Encrypt with aesx" for every file and folder, "Decrypt with aesx" for
/// <c>.aesx</c> files), and a small application that <c>.aesx</c> files open with and that files
/// can be dropped onto. The folders are parameters so that tests can use scratch ones instead of
/// somebody's real <c>~/Library/Services</c> and <c>~/Applications</c>.
/// </summary>
/// <remarks>
/// <para>
/// None of it is code of ours that runs inside the Finder. A service is an Automator workflow
/// bundle, two property lists that say "run this shell script with what is selected"; the
/// application is an AppleScript droplet that macOS compiles on the spot (<c>osacompile</c>).
/// Both do one thing: start the aesx that registered them, with <c>--finder</c> and the paths.
/// </para>
/// <para>
/// The workflows are written as plain services, without the two keys that make Automator call
/// something a Quick Action. A copied Quick Action stays switched off until its owner finds it
/// in System Settings; a service is in the menu at once.
/// </para>
/// </remarks>
/// <param name="services">Stands in for <c>~/Library/Services</c>.</param>
/// <param name="applications">Stands in for <c>~/Applications</c>.</param>
/// <param name="executable">Full path of aesx; not needed to unregister.</param>
internal sealed class FinderRegistration(string services, string applications, string executable)
{
    public const string EncryptEntry = "Encrypt with aesx";
    public const string DecryptEntry = "Decrypt with aesx";

    /// <summary>Identifies the application, and with a suffix each service; also how unregistering recognises what is ours.</summary>
    public const string Identifier = "io.github.yohasacura.aesx";

    /// <summary>The type that the application declares for <c>.aesx</c> files, and that the decrypting service asks for.</summary>
    public const string FileType = "io.github.yohasacura.aesx.encrypted-file";

    private const string AnyFileOrFolder = "public.item";
    private const string Compiler = "/usr/bin/osacompile";
    private const string PropertyLists = "/usr/bin/plutil";
    private const string Signer = "/usr/bin/codesign";

    public string EncryptService { get; } = Path.Combine(services, EncryptEntry + ".workflow");

    public string DecryptService { get; } = Path.Combine(services, DecryptEntry + ".workflow");

    public string Application { get; } = Path.Combine(applications, "aesx.app");

    /// <summary>Writes the services and the application, replacing earlier ones of ours.</summary>
    /// <exception cref="InvalidOperationException">Something of the same name is in the way that aesx did not write, or macOS could not build the application.</exception>
    public void Register()
    {
        if (executable.Any(char.IsControl))
            throw new InvalidOperationException("the path of aesx contains control characters, which a menu entry cannot carry; move aesx somewhere else");
        foreach (string path in (ReadOnlySpan<string>)[EncryptService, DecryptService, Application])
        {
            if (Directory.Exists(path) && !IsOurs(path))
                throw new InvalidOperationException($"{TextSafety.Display(path)} exists and was not written by aesx; move it away and run 'aesx register' again");
            if (File.Exists(path))
                throw new InvalidOperationException($"{TextSafety.Display(path)} is in the way; move it away and run 'aesx register' again");
        }

        // The application first: it is the part that needs macOS to do something, and if that
        // fails nothing is left half done.
        BuildApplication();
        WriteServices();
    }

    /// <summary>The two services alone; they are files and nothing else, so this works on any system.</summary>
    internal void WriteServices()
    {
        WriteService(EncryptService, EncryptEntry, "encrypt", AnyFileOrFolder);
        WriteService(DecryptService, DecryptEntry, "decrypt", FileType);
    }

    /// <summary>Removes what <see cref="Register"/> wrote and nothing else; missing pieces are not an error.</summary>
    /// <returns>Things that carry our names but were not written by aesx; they were left alone.</returns>
    public IReadOnlyList<string> Unregister()
    {
        var foreign = new List<string>();
        bool hadApplication = false;
        foreach (string path in (ReadOnlySpan<string>)[EncryptService, DecryptService, Application])
        {
            if (!Directory.Exists(path)) continue;
            if (!IsOurs(path))
            {
                foreign.Add(path);
                continue;
            }
            Directory.Delete(path, recursive: true);
            hadApplication |= path == Application;
        }

        // ~/Applications does not exist on a new account, so registering usually makes it. It is
        // not left behind empty.
        if (hadApplication)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(applications).Any()) Directory.Delete(applications);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return foreign;
    }

    /// <summary>True for a bundle whose Info.plist names one of our identifiers, whichever way the property list is stored.</summary>
    internal static bool IsOurs(string bundle)
    {
        try
        {
            return File.ReadAllBytes(Path.Combine(bundle, "Contents", "Info.plist")).AsSpan().IndexOf(Encoding.ASCII.GetBytes(Identifier)) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void WriteService(string bundle, string title, string command, string accepts)
    {
        if (Directory.Exists(bundle)) Directory.Delete(bundle, recursive: true);
        string contents = Path.Combine(bundle, "Contents");
        Directory.CreateDirectory(contents);

        // Unix line endings, however this source file was checked out.
        File.WriteAllText(Path.Combine(contents, "Info.plist"), ServiceInfo(title, command, accepts).ReplaceLineEndings("\n"));
        File.WriteAllText(Path.Combine(contents, "document.wflow"), Workflow(ServiceScript(command)).ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// What a service runs. Automator hands the selection over as arguments, all of it to one
    /// shell, so one aesx sees every item and asks once. aesx says what it has to say in a
    /// dialog of its own; the script always ends with success, because Automator would
    /// otherwise put a second, cryptic error message on top.
    /// </summary>
    internal string ServiceScript(string command) => $$"""
        aesx={{ShellQuoted(executable)}}
        if [ -x "$aesx" ]; then
            "$aesx" {{CommandLine.FinderSwitch}} {{command}} -- "$@" > /dev/null 2>&1
        else
            /usr/bin/osascript -e 'on run argv' -e 'display dialog "aesx is no longer at " & (item 1 of argv) & "." & return & return & "Run \"aesx register\" again from where it is now." with title "aesx" buttons {"OK"} default button "OK" with icon stop' -e 'end run' "$aesx" > /dev/null 2>&1
        fi
        exit 0
        """;

    /// <summary>How the Finder learns of the service: its title, and what has to be selected for it to be offered.</summary>
    private static string ServiceInfo(string title, string command, string accepts) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>CFBundleDevelopmentRegion</key>
            <string>en</string>
            <key>CFBundleIdentifier</key>
            <string>{Identifier}.{command}</string>
            <key>CFBundleName</key>
            <string>{Xml(title)}</string>
            <key>CFBundleShortVersionString</key>
            <string>{Program.Version}</string>
            <key>NSServices</key>
            <array>
                <dict>
                    <key>NSMenuItem</key>
                    <dict>
                        <key>default</key>
                        <string>{Xml(title)}</string>
                    </dict>
                    <key>NSMessage</key>
                    <string>runWorkflowAsService</string>
                    <key>NSRequiredContext</key>
                    <dict>
                        <key>NSApplicationIdentifier</key>
                        <string>com.apple.finder</string>
                    </dict>
                    <key>NSSendFileTypes</key>
                    <array>
                        <string>{accepts}</string>
                    </array>
                </dict>
            </array>
        </dict>
        </plist>

        """;

    /// <summary>
    /// The workflow itself, as Automator saves one: a single "Run Shell Script" action that
    /// receives files and folders from the Finder as arguments.
    /// </summary>
    private static string Workflow(string script) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>AMApplicationBuild</key>
            <string>523</string>
            <key>AMApplicationVersion</key>
            <string>2.10</string>
            <key>AMDocumentVersion</key>
            <string>2</string>
            <key>actions</key>
            <array>
                <dict>
                    <key>action</key>
                    <dict>
                        <key>AMAccepts</key>
                        <dict>
                            <key>Container</key>
                            <string>List</string>
                            <key>Optional</key>
                            <true/>
                            <key>Types</key>
                            <array>
                                <string>com.apple.cocoa.string</string>
                            </array>
                        </dict>
                        <key>AMActionVersion</key>
                        <string>2.0.3</string>
                        <key>AMApplication</key>
                        <array>
                            <string>Automator</string>
                        </array>
                        <key>AMParameterProperties</key>
                        <dict>
                            <key>COMMAND_STRING</key>
                            <dict/>
                            <key>CheckedForUserDefaultShell</key>
                            <dict/>
                            <key>inputMethod</key>
                            <dict/>
                            <key>shell</key>
                            <dict/>
                            <key>source</key>
                            <dict/>
                        </dict>
                        <key>AMProvides</key>
                        <dict>
                            <key>Container</key>
                            <string>List</string>
                            <key>Types</key>
                            <array>
                                <string>com.apple.cocoa.string</string>
                            </array>
                        </dict>
                        <key>ActionBundlePath</key>
                        <string>/System/Library/Automator/Run Shell Script.action</string>
                        <key>ActionName</key>
                        <string>Run Shell Script</string>
                        <key>ActionParameters</key>
                        <dict>
                            <key>COMMAND_STRING</key>
                            <string>{Xml(script)}</string>
                            <key>CheckedForUserDefaultShell</key>
                            <true/>
                            <key>inputMethod</key>
                            <integer>1</integer>
                            <key>shell</key>
                            <string>/bin/sh</string>
                            <key>source</key>
                            <string></string>
                        </dict>
                        <key>BundleIdentifier</key>
                        <string>com.apple.RunShellScript</string>
                        <key>CFBundleVersion</key>
                        <string>2.0.3</string>
                        <key>CanShowSelectedItemsWhenRun</key>
                        <false/>
                        <key>CanShowWhenRun</key>
                        <true/>
                        <key>Category</key>
                        <array>
                            <string>AMCategoryUtilities</string>
                        </array>
                        <key>Class Name</key>
                        <string>RunShellScriptAction</string>
                        <key>InputUUID</key>
                        <string>6A5E1F0C-9D3B-4E57-8C21-0B7F4D2A9E01</string>
                        <key>Keywords</key>
                        <array>
                            <string>Shell</string>
                            <string>Script</string>
                            <string>Command</string>
                            <string>Run</string>
                            <string>Unix</string>
                        </array>
                        <key>OutputUUID</key>
                        <string>6A5E1F0C-9D3B-4E57-8C21-0B7F4D2A9E02</string>
                        <key>UUID</key>
                        <string>6A5E1F0C-9D3B-4E57-8C21-0B7F4D2A9E03</string>
                        <key>UnlocalizedApplications</key>
                        <array>
                            <string>Automator</string>
                        </array>
                        <key>arguments</key>
                        <dict>
                            <key>0</key>
                            <dict>
                                <key>default value</key>
                                <integer>0</integer>
                                <key>name</key>
                                <string>inputMethod</string>
                                <key>required</key>
                                <string>0</string>
                                <key>type</key>
                                <string>0</string>
                                <key>uuid</key>
                                <string>0</string>
                            </dict>
                            <key>1</key>
                            <dict>
                                <key>default value</key>
                                <false/>
                                <key>name</key>
                                <string>CheckedForUserDefaultShell</string>
                                <key>required</key>
                                <string>0</string>
                                <key>type</key>
                                <string>0</string>
                                <key>uuid</key>
                                <string>1</string>
                            </dict>
                            <key>2</key>
                            <dict>
                                <key>default value</key>
                                <string></string>
                                <key>name</key>
                                <string>source</string>
                                <key>required</key>
                                <string>0</string>
                                <key>type</key>
                                <string>0</string>
                                <key>uuid</key>
                                <string>2</string>
                            </dict>
                            <key>3</key>
                            <dict>
                                <key>default value</key>
                                <string></string>
                                <key>name</key>
                                <string>COMMAND_STRING</string>
                                <key>required</key>
                                <string>0</string>
                                <key>type</key>
                                <string>0</string>
                                <key>uuid</key>
                                <string>3</string>
                            </dict>
                            <key>4</key>
                            <dict>
                                <key>default value</key>
                                <string>/bin/sh</string>
                                <key>name</key>
                                <string>shell</string>
                                <key>required</key>
                                <string>0</string>
                                <key>type</key>
                                <string>0</string>
                                <key>uuid</key>
                                <string>4</string>
                            </dict>
                        </dict>
                        <key>isViewVisible</key>
                        <integer>1</integer>
                        <key>location</key>
                        <string>309.000000:305.000000</string>
                        <key>nibPath</key>
                        <string>/System/Library/Automator/Run Shell Script.action/Contents/Resources/Base.lproj/main.nib</string>
                    </dict>
                    <key>isViewVisible</key>
                    <integer>1</integer>
                </dict>
            </array>
            <key>connectors</key>
            <dict/>
            <key>workflowMetaData</key>
            <dict>
                <key>serviceApplicationBundleID</key>
                <string>com.apple.finder</string>
                <key>serviceApplicationPath</key>
                <string>/System/Library/CoreServices/Finder.app</string>
                <key>serviceInputTypeIdentifier</key>
                <string>com.apple.Automator.fileSystemObject</string>
                <key>serviceOutputTypeIdentifier</key>
                <string>com.apple.Automator.nothing</string>
                <key>serviceProcessesInput</key>
                <integer>0</integer>
                <key>workflowTypeIdentifier</key>
                <string>com.apple.Automator.servicesMenu</string>
            </dict>
        </dict>
        </plist>

        """;

    /// <summary>
    /// Has macOS compile the droplet, then gives it a name of its own and the <c>.aesx</c> file
    /// type, and signs it again (ad hoc, like the original: a changed Info.plist would otherwise
    /// no longer match the signature).
    /// </summary>
    private void BuildApplication()
    {
        if (Directory.Exists(Application)) Directory.Delete(Application, recursive: true);
        Directory.CreateDirectory(applications);
        try
        {
            // Run-only: the application holds the compiled script and never rewrites itself.
            // Stay-open: it leaves when its own idle handler says so, not after the first event.
            MacTool.Run(Compiler, input: DropletScript().ReplaceLineEndings("\n"), "-x", "-s", "-o", Application);

            string info = Path.Combine(Application, "Contents", "Info.plist");
            MacTool.Run(PropertyLists, input: null, "-replace", "CFBundleIdentifier", "-string", Identifier, info);
            MacTool.Run(PropertyLists, input: null, "-replace", "CFBundleShortVersionString", "-string", Program.Version, info);
            MacTool.Run(PropertyLists, input: null, "-replace", "CFBundleDocumentTypes", "-json", DocumentTypes, info);
            MacTool.Run(PropertyLists, input: null, "-replace", "UTExportedTypeDeclarations", "-json", ExportedTypes, info);
            MacTool.Run(Signer, input: null, "--force", "--sign", "-", Application);
        }
        catch
        {
            try
            {
                if (Directory.Exists(Application)) Directory.Delete(Application, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            throw;
        }
    }

    // The application owns .aesx files (a double-click opens them with it) and takes anything
    // else that is dropped onto it, without offering itself in every file's "Open With" list.
    private const string DocumentTypes = $$$"""
        [
          {"CFBundleTypeName": "aesx encrypted file", "CFBundleTypeRole": "Viewer", "LSHandlerRank": "Owner", "LSItemContentTypes": ["{{{FileType}}}"]},
          {"CFBundleTypeName": "File or folder", "CFBundleTypeRole": "Viewer", "LSHandlerRank": "None", "LSItemContentTypes": ["public.item", "public.folder"]}
        ]
        """;

    private const string ExportedTypes = $$$"""
        [
          {"UTTypeIdentifier": "{{{FileType}}}", "UTTypeDescription": "aesx encrypted file", "UTTypeConformsTo": ["public.data"], "UTTypeTagSpecification": {"public.filename-extension": ["aesx"]}}
        ]
        """;

    /// <summary>
    /// The droplet, in AppleScript. Whatever is opened with it or dropped onto it goes to aesx,
    /// which decides by the name: <c>.aesx</c> is decrypted, everything else encrypted. aesx is
    /// started in the background, and from then on it is aesx that talks. The path of aesx is
    /// spelled as character codes, so the script is plain ASCII whatever the path contains.
    /// </summary>
    /// <remarks>
    /// macOS does not always hand a selection over in one piece: files that carry its "downloaded
    /// from the internet" mark arrive as an event of their own, right after the others. A droplet
    /// that quit after its first event could miss them, so this one stays for a few seconds
    /// after the last event and then leaves by itself. Each piece gets an aesx of its own.
    /// </remarks>
    internal string DropletScript()
    {
        string codes = string.Join(", ", executable.EnumerateRunes().Select(rune => rune.Value.ToString(CultureInfo.InvariantCulture)));
        return $$"""
        property lastWork : missing value

        on open theItems
            hand(theItems)
            set lastWork to current date
        end open

        on run
            hand(choose file with prompt "Choose files to encrypt, or .aesx files to decrypt. (Folders: drop them onto aesx, or right-click them in the Finder.)" with multiple selections allowed)
        end run

        on idle
            if lastWork is not missing value and ((current date) - lastWork) < 3 then return 1
            quit
            return 1
        end idle

        on hand(theItems)
            set aesx to string id { {{codes}} }
            try
                do shell script "test -x " & quoted form of aesx
            on error
                display dialog "aesx is no longer at " & aesx & "." & return & return & "Run \"aesx register\" again from where it is now." with title "aesx" buttons {"OK"} default button "OK" with icon stop
                return
            end try
            set command to quoted form of aesx & " {{CommandLine.FinderSwitch}} --"
            repeat with anItem in theItems
                set command to command & space & quoted form of POSIX path of anItem
            end repeat
            do shell script command & " > /dev/null 2>&1 &"
        end hand

        """;
    }

    /// <summary>One word for a POSIX shell, whatever it contains.</summary>
    internal static string ShellQuoted(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string Xml(string text)
        => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}

/// <summary>Runs one of the programs macOS ships, by its full path.</summary>
internal static class MacTool
{
    private static readonly TimeSpan s_patience = TimeSpan.FromMinutes(2);

    /// <summary>Runs the program and waits for it; anything but success is an error that says what the program said.</summary>
    public static void Run(string program, string? input, params string[] arguments)
    {
        (int exitCode, string said) = Start(program, input, arguments);
        if (exitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(program)} failed with code {exitCode}{(said.Length > 0 ? $": {TextSafety.Display(said)}" : "")}");
    }

    /// <summary>For what is only a courtesy: runs the program and does not mind if it cannot be run or fails.</summary>
    public static void Try(string program, params string[] arguments)
    {
        try
        {
            _ = Start(program, input: null, arguments);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static (int ExitCode, string Said) Start(string program, string? input, string[] arguments)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException($"cannot start {program}");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using (Stream stdin = process.StandardInput.BaseStream)
            {
                if (input is not null) stdin.Write(Encoding.ASCII.GetBytes(input));
            }
            if (!process.WaitForExit(s_patience))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException($"{Path.GetFileName(program)} did not finish within {s_patience.TotalMinutes:F0} minutes");
            }
            string said = (error.GetAwaiter().GetResult() + output.GetAwaiter().GetResult()).Trim();
            return (process.ExitCode, said);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            throw new InvalidOperationException($"cannot run {program}: {ex.Message}", ex);
        }
    }
}
