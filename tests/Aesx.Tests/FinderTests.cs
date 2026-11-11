// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Aesx.Cli;

namespace Aesx.Tests;

/// <summary>
/// The Finder integration on macOS: what registering writes, the dialogs, and the real thing
/// end to end where a Mac lets a test operate its windows.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here touches somebody's real <c>~/Library/Services</c> or <c>~/Applications</c>: what
/// registering writes goes into scratch folders. The services are plain files, so what is in
/// them is checked on every system; the script inside is run by a real shell wherever there is
/// one. The dialogs are checked twice: their logic everywhere but Windows, against a stand-in
/// for <c>osascript</c>, and on a Mac for real, with the test typing into the dialog that macOS
/// draws.
/// </para>
/// <para>
/// Typing into another program's window needs the Mac's permission (Privacy &amp; Security,
/// Accessibility). GitHub's runners have it, and there these tests have to pass; on a Mac
/// without it they stand aside. A real <c>aesx register</c> is exercised by
/// <c>tests/scenarios/finder.sh</c>, which CI runs against the published binary.
/// </para>
/// </remarks>
public class FinderTests
{
    private const string Pw = "finder-password ✓ пароль";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    [Fact]
    public void RegisteringWritesTwoServicesThatTheFinderOffersAtOnce()
    {
        using var dir = new TempDir();
        var registration = new FinderRegistration(dir.File("Services"), dir.File("Applications"), "/Users/someone/My Tools/aesx");
        registration.WriteServices();

        Assert.Equal(["Decrypt with aesx.workflow", "Encrypt with aesx.workflow"], Directory.GetDirectories(dir.File("Services")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach ((string bundle, string title, string command, string accepts) in new[]
        {
            (registration.EncryptService, "Encrypt with aesx", "encrypt", "public.item"),
            (registration.DecryptService, "Decrypt with aesx", "decrypt", "io.github.yohasacura.aesx.encrypted-file"),
        })
        {
            Assert.Equal(["Contents/Info.plist", "Contents/document.wflow"], Directory.GetFiles(bundle, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(bundle, f).Replace('\\', '/')).Order(StringComparer.Ordinal));

            object info = Plist.Read(Path.Combine(bundle, "Contents", "Info.plist"));
            Assert.Equal($"io.github.yohasacura.aesx.{command}", Plist.At(info, "CFBundleIdentifier"));
            object service = Plist.At(info, "NSServices", 0);
            Assert.Equal(title, Plist.At(service, "NSMenuItem", "default"));
            Assert.Equal("runWorkflowAsService", Plist.At(service, "NSMessage"));
            Assert.Equal("com.apple.finder", Plist.At(service, "NSRequiredContext", "NSApplicationIdentifier"));
            Assert.Equal([accepts], (List<object>)Plist.At(service, "NSSendFileTypes"));

            // With either of these macOS files the workflow under Quick Actions, where it stays
            // switched off until somebody finds it in System Settings.
            var keys = (Dictionary<string, object>)service;
            Assert.DoesNotContain("NSIconName", keys.Keys);
            Assert.DoesNotContain("NSBackgroundColorName", keys.Keys);

            object workflow = Plist.Read(Path.Combine(bundle, "Contents", "document.wflow"));
            Assert.Equal("com.apple.Automator.servicesMenu", Plist.At(workflow, "workflowMetaData", "workflowTypeIdentifier"));
            Assert.Equal("com.apple.finder", Plist.At(workflow, "workflowMetaData", "serviceApplicationBundleID"));
            Assert.Single((List<object>)Plist.At(workflow, "actions"));
            object action = Plist.At(workflow, "actions", 0, "action");
            Assert.Equal("com.apple.RunShellScript", Plist.At(action, "BundleIdentifier"));
            Assert.Equal("/System/Library/Automator/Run Shell Script.action", Plist.At(action, "ActionBundlePath"));
            Assert.Equal(1L, Plist.At(action, "ActionParameters", "inputMethod")); // the selection arrives as arguments
            Assert.Equal("/bin/sh", Plist.At(action, "ActionParameters", "shell"));

            string script = (string)Plist.At(action, "ActionParameters", "COMMAND_STRING");
            Assert.Contains("aesx='/Users/someone/My Tools/aesx'\n", script, StringComparison.Ordinal);
            Assert.Contains($"\"$aesx\" --finder {command} -- \"$@\" > /dev/null 2>&1\n", script, StringComparison.Ordinal);
            Assert.EndsWith("exit 0", script, StringComparison.Ordinal);
            Assert.DoesNotContain("\r", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheScriptOfAServiceStartsAesxWithTheSelectionWhateverThePathsLookLike()
    {
        if (OperatingSystem.IsWindows()) return; // needs a POSIX shell
        using var dir = new TempDir();

        // Everything a shell or an XML parser might trip over, in the path of aesx itself.
        string home = dir.File("it's \"here\" & <there> $HOME `tick` пробел 🔐");
        Directory.CreateDirectory(home);
        string aesx = Path.Combine(home, "aesx");
        FakeAesx.Write(aesx);

        var registration = new FinderRegistration(dir.File("Services"), dir.File("Applications"), aesx);
        registration.WriteServices();
        string script = (string)Plist.At(Plist.Read(Path.Combine(registration.EncryptService, "Contents", "document.wflow")), "actions", 0, "action", "ActionParameters", "COMMAND_STRING");

        string[] selection = ["/Users/someone/a file.txt", "/tmp/второй 🔐.txt", "/tmp/it's \"quoted\" & $odd/", "-dash"];
        Assert.Equal(0, await RunShellAsync(script, selection));
        string[] received = await FakeAesx.ArgumentsAsync(aesx, Patience);
        Assert.Equal(["--finder", "encrypt", "--", .. selection], received);

        // Once aesx is gone the script says so in a dialog (there is no osascript to show one
        // here, which is the point of trying it only where there is none) and still succeeds:
        // Automator must not add an error message of its own.
        if (OperatingSystem.IsMacOS()) return;
        File.Delete(aesx);
        File.Delete(aesx + ".args");
        Assert.Equal(0, await RunShellAsync(script, selection));
        Assert.False(File.Exists(aesx + ".args"));
    }

    [Fact]
    public void UnregisteringRemovesWhatWasWrittenAndLeavesTheRestAlone()
    {
        using var dir = new TempDir();
        string services = dir.File("Services"), applications = dir.File("Applications");
        var registration = new FinderRegistration(services, applications, "/usr/local/bin/aesx");
        registration.WriteServices();
        WriteBundle(registration.Application, "<string>io.github.yohasacura.aesx</string>"); // what building the droplet leaves, as far as removal cares
        WriteBundle(Path.Combine(services, "Somebody else.workflow"), "<string>their workflow</string>");

        Assert.Empty(registration.Unregister());
        Assert.Equal(["Somebody else.workflow"], Directory.GetFileSystemEntries(services).Select(Path.GetFileName));
        Assert.False(Directory.Exists(applications)); // registering had to create it; it is not left behind empty

        Assert.Empty(registration.Unregister()); // and once more is harmless
        Assert.Equal(["Somebody else.workflow"], Directory.GetFileSystemEntries(services).Select(Path.GetFileName));
    }

    [Fact]
    public void WhatCarriesOurNamesButIsNotOursIsNeverTouched()
    {
        using var dir = new TempDir();
        string services = dir.File("Services"), applications = dir.File("Applications");
        var registration = new FinderRegistration(services, applications, "/usr/local/bin/aesx");
        WriteBundle(registration.DecryptService, "<string>com.example.somebody-else</string>");
        WriteBundle(registration.Application, "<string>com.example.another-aesx</string>");
        File.WriteAllText(Path.Combine(applications, "Their notes.txt"), "keep");

        // Registering refuses before it writes or builds anything, on any system.
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(registration.Register);
        Assert.Contains("was not written by aesx", refusal.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(registration.EncryptService));

        registration.WriteServices(); // suppose the services had been ours all along
        WriteBundle(registration.DecryptService, "<string>com.example.somebody-else</string>");
        Assert.Equal([registration.DecryptService, registration.Application], registration.Unregister());
        Assert.False(Directory.Exists(registration.EncryptService));
        Assert.Contains("somebody-else", File.ReadAllText(Path.Combine(registration.DecryptService, "Contents", "Info.plist")), StringComparison.Ordinal);
        Assert.Contains("another-aesx", File.ReadAllText(Path.Combine(registration.Application, "Contents", "Info.plist")), StringComparison.Ordinal);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(applications, "Their notes.txt")));
    }

    [Fact]
    public void APathThatAMenuEntryCannotCarryIsRefused()
    {
        using var dir = new TempDir();
        var registration = new FinderRegistration(dir.File("Services"), dir.File("Applications"), "/Users/someone/bin\n/aesx");
        Assert.Contains("control characters", Assert.Throws<InvalidOperationException>(registration.Register).Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public void TheEntriesPointAtTheLinkThatAPackageManagerKeepsOnThePath()
    {
        if (OperatingSystem.IsWindows()) return; // symbolic links need a privilege there, and this is about Homebrew
        using var dir = new TempDir();
        string real = dir.File("Cellar/aesx/1.1.0/bin/aesx");
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        File.WriteAllText(real, "");
        Directory.CreateDirectory(dir.File("bin"));
        Directory.CreateDirectory(dir.File("elsewhere"));
        File.CreateSymbolicLink(dir.File("bin/aesx"), "../Cellar/aesx/1.1.0/bin/aesx");
        File.WriteAllText(dir.File("elsewhere/aesx"), "another program of the same name");

        string path = string.Join(':', "relative/dir", dir.File("missing"), dir.File("elsewhere"), dir.File("bin"), Path.GetDirectoryName(real));
        Assert.Equal(dir.File("bin/aesx"), FinderIntegration.StableExecutablePath(real, path));

        // Without such a link the executable itself is what there is to point at.
        Assert.Equal(real, FinderIntegration.StableExecutablePath(real, string.Join(':', dir.File("elsewhere"), Path.GetDirectoryName(real))));
        Assert.Equal(real, FinderIntegration.StableExecutablePath(real, null));
    }

    [Fact]
    public void DecryptingAsksOnceAndTakesTheAnswerAsItIs()
    {
        if (OperatingSystem.IsWindows()) return; // the stand-in is a shell script
        using var osascript = new StandIn();
        osascript.Answers("+correct horse ✓ пароль 🔐\n");

        char[] password = new FinderDialogs(osascript.Path).AskPassword("Decrypt report.pdf.aesx", confirm: false);

        Assert.Equal("correct horse ✓ пароль 🔐", new string(password));
        Assert.Equal(1, osascript.Calls);
        Assert.Equal(["-", "Decrypt report.pdf.aesx\n\nPassword:"], osascript.Arguments(1)); // the script on standard input, the text as an argument
        Assert.Contains("with hidden answer", osascript.Script(1), StringComparison.Ordinal);
        Assert.DoesNotContain("report.pdf", osascript.Script(1), StringComparison.Ordinal); // a file name never becomes part of a script
    }

    [Theory]
    [InlineData("+ two  spaces \n", " two  spaces ")]
    [InlineData("++starts with a plus\n", "+starts with a plus")]
    [InlineData("+-starts with a minus\n", "-starts with a minus")]
    [InlineData("+no line feed at the end", "no line feed at the end")]
    [InlineData("+a line feed\nin the middle\n", "a line feed\nin the middle")]
    public void OnlyTheMarkInFrontAndTheLastLineFeedAreNotPartOfThePassword(string printed, string expected)
    {
        if (OperatingSystem.IsWindows()) return;
        using var osascript = new StandIn();
        osascript.Answers(printed);
        Assert.Equal(expected, new string(new FinderDialogs(osascript.Path).AskPassword("Decrypt x.aesx", confirm: false)));
    }

    [Fact]
    public void EncryptingAsksTwiceAndComparesExactly()
    {
        if (OperatingSystem.IsWindows()) return;
        using var osascript = new StandIn();

        // AppleScript would call the first two the same; a password is not case-insensitive.
        osascript.Answers("+Secret\n", "+secret\n", "+Secret\n", "+Secret\n");

        char[] password = new FinderDialogs(osascript.Path).AskPassword("Encrypt 3 items", confirm: true);

        Assert.Equal("Secret", new string(password));
        Assert.Equal(4, osascript.Calls);
        Assert.Equal("Encrypt 3 items\n\nPassword:", osascript.Arguments(1)[1]);
        Assert.Equal("Encrypt 3 items\n\nThe same password once more:", osascript.Arguments(2)[1]);
        Assert.Equal("The two passwords were not the same.\n\nEncrypt 3 items\n\nPassword:", osascript.Arguments(3)[1]);
        Assert.Equal("Encrypt 3 items\n\nThe same password once more:", osascript.Arguments(4)[1]);
    }

    [Fact]
    public void AnEmptyPasswordIsAskedForAgain()
    {
        if (OperatingSystem.IsWindows()) return;
        using var osascript = new StandIn();
        osascript.Answers("+\n", "+x\n");

        Assert.Equal("x", new string(new FinderDialogs(osascript.Path).AskPassword("Decrypt a.aesx", confirm: false)));
        Assert.Equal("The password must not be empty.\n\nDecrypt a.aesx\n\nPassword:", osascript.Arguments(2)[1]);
    }

    [Fact]
    public void CancelIsAnAnswerAndNotAnError()
    {
        if (OperatingSystem.IsWindows()) return;
        using (var osascript = new StandIn())
        {
            osascript.Answers("-\n");
            Assert.Throws<PromptCancelledException>(() => new FinderDialogs(osascript.Path).AskPassword("Encrypt a.txt", confirm: true));
            Assert.Equal(1, osascript.Calls);
        }
        using (var osascript = new StandIn())
        {
            osascript.Answers("+typed once\n", "-\n"); // cancelled when asked to repeat it
            Assert.Throws<PromptCancelledException>(() => new FinderDialogs(osascript.Path).AskPassword("Encrypt a.txt", confirm: true));
            Assert.Equal(2, osascript.Calls);
        }
    }

    [Fact]
    public void WhenNoDialogCanBeShownTheErrorSaysWhy()
    {
        if (OperatingSystem.IsWindows()) return;
        using var osascript = new StandIn();
        osascript.Fails(1, "execution error: No user interaction allowed. (-1713)\n");
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => new FinderDialogs(osascript.Path).AskPassword("Encrypt a.txt", confirm: true));
        Assert.Contains("No user interaction allowed. (-1713)", error.Message, StringComparison.Ordinal);

        // Anything that is neither a password nor a cancellation is no answer.
        osascript.Answers(2, "button returned:OK\n");
        Assert.Throws<InvalidOperationException>(() => new FinderDialogs(osascript.Path).AskPassword("Encrypt a.txt", confirm: true));

        var missing = new FinderDialogs(Path.Combine(osascript.Directory, "no such program"));
        Assert.Contains("cannot run", Assert.Throws<InvalidOperationException>(() => missing.AskPassword("Encrypt a.txt", confirm: true)).Message, StringComparison.Ordinal);
        missing.Tell(["error: nobody will see this in a window"], failed: true); // it is on standard error all the same
    }

    [Fact]
    public void WhatWentWrongIsToldInOneDialog()
    {
        if (OperatingSystem.IsWindows()) return;
        using var osascript = new StandIn();
        var dialogs = new FinderDialogs(osascript.Path);

        dialogs.Tell(["error: a.txt.aesx: wrong password, or the file is corrupted", "error: b.txt.aesx: wrong password, or the file is corrupted"], failed: true);
        dialogs.Tell(Enumerable.Range(1, 20).Select(i => $"warning: number {i}").ToList(), failed: false);

        Assert.Equal(["-", "error: a.txt.aesx: wrong password, or the file is corrupted\nerror: b.txt.aesx: wrong password, or the file is corrupted", "failed"], osascript.Arguments(1));
        Assert.Contains("with icon stop", osascript.Script(1), StringComparison.Ordinal);
        Assert.Equal("warning", osascript.Arguments(2)[2]);
        Assert.StartsWith("warning: number 1\nwarning: number 2\n", osascript.Arguments(2)[1], StringComparison.Ordinal);
        Assert.EndsWith("warning: number 15\n... and 5 more.", osascript.Arguments(2)[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFinderSwitchExistsOnAMacOnly()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("a.txt"), "a");

        // A password from the environment needs no dialog, which is also how a script of
        // somebody's own can use the switch.
        var run = new CliRun { Environment = new Dictionary<string, string?> { [Cli.PasswordVariable] = Pw } };
        CliResult result = await Cli.RunWithAsync(run, "--finder", "encrypt", "--", dir.File("a.txt"));
        CliResult help = await Cli.RunAsync(null, "--help");
        if (OperatingSystem.IsMacOS())
        {
            Assert.True(result.ExitCode == 0, result.ToString());
            Assert.True(File.Exists(dir.File("a.txt.aesx")));
            Assert.Contains("--finder", help.Stdout, StringComparison.Ordinal);
            Assert.Equal(2, (await Cli.RunAsync(null, "--finder", "register")).ExitCode);
        }
        else
        {
            Assert.Equal(2, result.ExitCode);
            Assert.Contains("unknown option '--finder'", result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("--finder", help.Stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OnAMacTheServicesAreWhatItsOwnToolsAccept()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var dir = new TempDir();
        var registration = new FinderRegistration(dir.File("Services"), dir.File("Applications"), "/Users/someone/it's & <here>/aesx");
        registration.WriteServices();
        foreach (string bundle in new[] { registration.EncryptService, registration.DecryptService })
        {
            foreach (string file in new[] { "Info.plist", "document.wflow" })
            {
                ToolResult lint = await Mac.RunAsync("/usr/bin/plutil", null, "-lint", Path.Combine(bundle, "Contents", file));
                Assert.True(lint.ExitCode == 0, lint.ToString());
            }
        }
    }

    [Fact]
    public async Task OnAMacRegisteringBuildsADropletThatOwnsAesxFilesAndUnregisteringLeavesNoTrace()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var dir = new TempDir();
        var registration = new FinderRegistration(dir.File("Services"), dir.File("Applications"), "/Users/someone/My Tools/aesx");
        registration.Register();

        // A droplet, as macOS built it; ours by name and by what it declares; and signed again
        // after the changes, or macOS would call it damaged.
        Assert.True(File.Exists(Path.Combine(registration.Application, "Contents", "MacOS", "droplet")));
        Assert.True(File.Exists(Path.Combine(registration.Application, "Contents", "Resources", "Scripts", "main.scpt")));
        ToolResult signature = await Mac.RunAsync("/usr/bin/codesign", null, "--verify", "--deep", "--strict", registration.Application);
        Assert.True(signature.ExitCode == 0, signature.ToString());

        ToolResult converted = await Mac.RunAsync("/usr/bin/plutil", null, "-convert", "json", "-o", "-", Path.Combine(registration.Application, "Contents", "Info.plist"));
        Assert.True(converted.ExitCode == 0, converted.ToString());
        using JsonDocument info = JsonDocument.Parse(converted.Output);
        Assert.Equal("io.github.yohasacura.aesx", info.RootElement.GetProperty("CFBundleIdentifier").GetString());
        Assert.Equal("droplet", info.RootElement.GetProperty("CFBundleExecutable").GetString());
        JsonElement owned = info.RootElement.GetProperty("CFBundleDocumentTypes")[0];
        Assert.Equal("Owner", owned.GetProperty("LSHandlerRank").GetString());
        Assert.Equal("io.github.yohasacura.aesx.encrypted-file", owned.GetProperty("LSItemContentTypes")[0].GetString());
        JsonElement dropped = info.RootElement.GetProperty("CFBundleDocumentTypes")[1];
        Assert.Equal("None", dropped.GetProperty("LSHandlerRank").GetString()); // takes drops, stays out of every file's "Open With"
        JsonElement type = info.RootElement.GetProperty("UTExportedTypeDeclarations")[0];
        Assert.Equal("io.github.yohasacura.aesx.encrypted-file", type.GetProperty("UTTypeIdentifier").GetString());
        Assert.Equal("aesx", type.GetProperty("UTTypeTagSpecification").GetProperty("public.filename-extension")[0].GetString());

        Assert.True(Directory.Exists(registration.EncryptService) && Directory.Exists(registration.DecryptService));

        // Registering again, from another place, replaces all three.
        new FinderRegistration(dir.File("Services"), dir.File("Applications"), "/opt/homebrew/bin/aesx").Register();
        Assert.Contains("aesx='/opt/homebrew/bin/aesx'", await File.ReadAllTextAsync(Path.Combine(registration.EncryptService, "Contents", "document.wflow")), StringComparison.Ordinal);
        signature = await Mac.RunAsync("/usr/bin/codesign", null, "--verify", "--deep", "--strict", registration.Application);
        Assert.True(signature.ExitCode == 0, signature.ToString());

        Assert.Empty(registration.Unregister());
        Assert.Empty(Directory.GetFileSystemEntries(dir.File("Services")));
        Assert.False(Directory.Exists(dir.File("Applications")));
    }

    [Fact]
    public async Task OnAMacWhatIsDroppedOnTheDropletReachesOneAesx()
    {
        if (!OperatingSystem.IsMacOS() || Mac.IsRegisteredForReal()) return;
        using var dir = new TempDir();

        // The droplet spells this path out character by character, so it gets a hard one.
        string home = dir.File("it's \"here\" & <there> пробел 🔐");
        Directory.CreateDirectory(home);
        string aesx = Path.Combine(home, "aesx");
        FakeAesx.Write(aesx);
        Directory.CreateDirectory(dir.File("a folder"));
        string[] dropped = [dir.File("one.txt"), dir.File("три 🔐.aesx"), dir.File("a folder")];
        foreach (string file in dropped[..2]) await File.WriteAllTextAsync(file, "x");

        var registration = new FinderRegistration(dir.File("Services"), dir.File("Applications"), aesx);
        registration.Register();
        try
        {
            // What a drop onto the icon, or a double-click on the files, amounts to.
            ToolResult opened = await Mac.RunAsync("/usr/bin/open", null, ["-a", registration.Application, .. dropped]);
            Assert.True(opened.ExitCode == 0, opened.ToString());

            string[] arguments = await FakeAesx.ArgumentsAsync(aesx, Patience);
            Assert.Equal(["--finder", "--"], arguments[..2]);
            Assert.Equal(dropped.Select(Path.GetFileName), arguments[2..].Select(a => Path.GetFileName(Path.TrimEndingDirectorySeparator(a))));
            Assert.All(arguments[2..], a => Assert.True(File.Exists(a) || Directory.Exists(a), a));

            // It stays a few seconds, for the rest of a selection that macOS delivers in parts,
            // and then leaves by itself instead of sitting in the Dock.
            long started = Stopwatch.GetTimestamp();
            while ((await Mac.RunAsync("/usr/bin/pgrep", null, "-f", "aesx.app/Contents/MacOS/droplet")).ExitCode == 0)
            {
                Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(45), "the droplet is still running");
                await Task.Delay(500);
            }
        }
        finally
        {
            // Opening an application makes it known to the system; this takes it back.
            await Mac.RunAsync(Mac.LaunchServices, null, "-u", registration.Application);
            registration.Unregister();
        }
    }

    [Fact]
    public async Task OnAMacSeveralItemsAreEncryptedByOneProcessAskingOnce()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var dir = new TempDir();

        // Four items, where the test with Automator has three. The dialogs are found by what
        // they say, and no two tests should be waiting for the same words.
        string[] files = [dir.File("one.txt"), dir.File("two.txt"), dir.File("три 🔐.txt")];
        foreach (string file in files) await File.WriteAllTextAsync(file, "contents of " + Path.GetFileName(file));
        Directory.CreateDirectory(dir.File("photos"));
        await File.WriteAllTextAsync(dir.File("photos/a.txt"), "a");

        using Process aesx = Process.Start(Cli.StartInfo(new CliRun(), ["--finder", "encrypt", "--", .. files, dir.File("photos")]))!;
        Task<string> errors = aesx.StandardError.ReadToEndAsync();
        Task<string> output = aesx.StandardOutput.ReadToEndAsync();
        try
        {
            if (!await Mac.AnswerAsync(["Encrypt 4 items", FinderDialogs.PasswordLabel], Pw, "OK", Patience, aesx)) return;
            Assert.True(await Mac.AnswerAsync(["Encrypt 4 items", FinderDialogs.ConfirmLabel], Pw, "OK", Patience));
            await aesx.WaitForExitAsync(new CancellationTokenSource(Patience).Token);
            Assert.True(aesx.ExitCode == 0, $"exit {aesx.ExitCode}\n{await output}\n{await errors}");
        }
        finally
        {
            if (!aesx.HasExited) aesx.Kill(entireProcessTree: true);
        }

        foreach (string file in files)
        {
            CliResult dec = await Cli.RunAsync(null, "decrypt", file + ".aesx", "-p", Pw, "-o", "-", "-q");
            Assert.True(dec.ExitCode == 0, dec.ToString());
            Assert.Equal("contents of " + Path.GetFileName(file), dec.Stdout);
        }
        Assert.True(File.Exists(dir.File("photos.folder.aesx")));
    }

    [Fact]
    public async Task OnAMacAWrongPasswordIsReportedInADialog()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("secret.txt"), "secret");
        Assert.Equal(0, (await Cli.RunAsync(null, "encrypt", dir.File("secret.txt"), "-p", Pw, "--delete", "--iterations", "1000")).ExitCode);

        using Process aesx = Process.Start(Cli.StartInfo(new CliRun(), "--finder", "decrypt", "--", dir.File("secret.txt.aesx")))!;
        Task<string> errors = aesx.StandardError.ReadToEndAsync();
        _ = aesx.StandardOutput.ReadToEndAsync();
        try
        {
            // Decrypting asks once: what comes after the first answer is the report.
            if (!await Mac.AnswerAsync(["Decrypt secret.txt.aesx", FinderDialogs.PasswordLabel], "not the password", "OK", Patience, aesx)) return;
            Assert.True(await Mac.AnswerAsync(["wrong password"], null, "OK", Patience));
            await aesx.WaitForExitAsync(new CancellationTokenSource(Patience).Token);
            Assert.Equal(1, aesx.ExitCode);
            Assert.Contains("wrong password", await errors, StringComparison.Ordinal);
        }
        finally
        {
            if (!aesx.HasExited) aesx.Kill(entireProcessTree: true);
        }
        Assert.Equal(["secret.txt.aesx"], dir.Files());
    }

    [Fact]
    public async Task OnAMacPasswordsThatDifferAreAskedForAgainAndCancelDoesNothingAtAll()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("untouched.txt"), "x");

        using Process aesx = Process.Start(Cli.StartInfo(new CliRun(), "--finder", "--", dir.File("untouched.txt")))!;
        _ = aesx.StandardError.ReadToEndAsync();
        _ = aesx.StandardOutput.ReadToEndAsync();
        try
        {
            if (!await Mac.AnswerAsync(["Encrypt untouched.txt", FinderDialogs.PasswordLabel], Pw, "OK", Patience, aesx)) return;
            Assert.True(await Mac.AnswerAsync(["Encrypt untouched.txt", FinderDialogs.ConfirmLabel], Pw + " with a typo", "OK", Patience));
            Assert.True(await Mac.AnswerAsync([FinderDialogs.MismatchComplaint, FinderDialogs.PasswordLabel], null, "Cancel", Patience));
            await aesx.WaitForExitAsync(new CancellationTokenSource(Patience).Token);
            Assert.Equal(130, aesx.ExitCode);
        }
        finally
        {
            if (!aesx.HasExited) aesx.Kill(entireProcessTree: true);
        }
        Assert.Equal(["untouched.txt"], dir.Files());
    }

    [Fact]
    public async Task OnAMacAutomatorRunsTheServicesAndEachAsksOnceForTheWholeSelection()
    {
        if (!OperatingSystem.IsMacOS() || Cli.Executable is not { } executable) return;
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("notes.txt"), "notes");
        await File.WriteAllTextAsync(dir.File("план 🔐.txt"), "plan");
        Directory.CreateDirectory(dir.File("photos"));
        await File.WriteAllTextAsync(dir.File("photos/a.txt"), "a");

        // What Automator starts does not get the environment of this test run, and the aesx that
        // the tests build has to be told where the .NET runtime is (a published one carries its
        // own). So the services point at a script named aesx that says so and passes everything
        // on, from a folder whose name a shell has to take care with.
        string launcher = dir.File("it's a launcher/aesx");
        Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
        string runtime = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? Path.GetDirectoryName(Environment.ProcessPath) ?? "";
        await File.WriteAllTextAsync(launcher, $"#!/bin/sh\nDOTNET_ROOT={FinderRegistration.ShellQuoted(runtime)}\nexport DOTNET_ROOT\nexec {FinderRegistration.ShellQuoted(executable)} \"$@\" 2>> \"$0.errors\"\n");
        File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var registration = new FinderRegistration(dir.File("Services"), dir.File("Applications"), launcher);
        registration.WriteServices();

        try
        {
            // Automator itself loads the workflow and runs it with a selection, as it does when
            // the entry is chosen in the Finder.
            Task<ToolResult> encrypting = Mac.RunAsync("/usr/bin/automator", $"{dir.File("notes.txt")}\n{dir.File("план 🔐.txt")}\n{dir.File("photos")}\n", "-i", "-", registration.EncryptService);
            try
            {
                if (!await Mac.AnswerAsync(["Encrypt 3 items", FinderDialogs.PasswordLabel], Pw, "OK", Patience)) return;
            }
            catch (TimeoutException ex)
            {
                string errors = File.Exists(launcher + ".errors") ? await File.ReadAllTextAsync(launcher + ".errors") : "(nothing)";
                throw new TimeoutException($"{ex.Message}\n--- what aesx wrote ---\n{errors}\n--- automator ---\n{(encrypting.IsCompleted ? (await encrypting).ToString() : "(still running)")}", ex);
            }
            Assert.True(await Mac.AnswerAsync(["Encrypt 3 items", FinderDialogs.ConfirmLabel], Pw, "OK", Patience));
            ToolResult encrypted = await encrypting;
            Assert.True(encrypted.ExitCode == 0, encrypted.ToString());
            Assert.Equal(["notes.txt", "notes.txt.aesx", "photos", "photos.folder.aesx", "план 🔐.txt", "план 🔐.txt.aesx"], Entries(dir.Path));

            File.Delete(dir.File("notes.txt"));
            Directory.Delete(dir.File("photos"), recursive: true);
            Task<ToolResult> decrypting = Mac.RunAsync("/usr/bin/automator", $"{dir.File("notes.txt.aesx")}\n{dir.File("photos.folder.aesx")}\n", "-i", "-", registration.DecryptService);
            Assert.True(await Mac.AnswerAsync(["Decrypt 2 items", FinderDialogs.PasswordLabel], Pw, "OK", Patience));
            ToolResult decrypted = await decrypting;
            Assert.True(decrypted.ExitCode == 0, decrypted.ToString());
            Assert.Equal("notes", await File.ReadAllTextAsync(dir.File("notes.txt")));
            Assert.Equal("a", await File.ReadAllTextAsync(dir.File("photos/a.txt")));
        }
        finally
        {
            // What Automator started is no child of this process, and none of it may outlive the
            // test: not aesx, and not a dialog it was showing, which is a process of its own and
            // would be left on the screen for a later test to take for the one it is waiting for.
            // Killed outright, aesx does not get to report in one more dialog that it was stopped.
            string[] started = Pids(await Mac.RunAsync("/usr/bin/pgrep", null, "-f", executable + " --finder"));
            var dialogs = new List<string>();
            foreach (string pid in started) dialogs.AddRange(Pids(await Mac.RunAsync("/usr/bin/pgrep", null, "-P", pid)));
            if (started.Length > 0) await Mac.RunAsync("/bin/kill", null, ["-9", .. started]);
            if (dialogs.Count > 0) await Mac.RunAsync("/bin/kill", null, ["-9", .. dialogs]);
        }

        static string[] Pids(ToolResult found) => found.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        static string[] Entries(string path) => Directory.GetFileSystemEntries(path).Select(e => Path.GetFileName(e)!).Where(n => n is not ("Services" or "Applications" or "it's a launcher")).Order(StringComparer.Ordinal).ToArray();
    }

    private static void WriteBundle(string bundle, string identity)
    {
        Directory.CreateDirectory(Path.Combine(bundle, "Contents"));
        File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"), $"<plist><dict><key>CFBundleIdentifier</key>{identity}</dict></plist>");
    }

    /// <summary>Runs a script the way Automator's "Run Shell Script" does with the input as arguments.</summary>
    private static async Task<int> RunShellAsync(string script, string[] arguments)
    {
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardInput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("-"); // what Automator passes as the script's own name
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process shell = Process.Start(start)!;
        shell.StandardInput.Close();
        await shell.WaitForExitAsync(new CancellationTokenSource(Patience).Token);
        return shell.ExitCode;
    }

    /// <summary>A program called aesx that only writes down what it was started with, next to itself.</summary>
    private static class FakeAesx
    {
        [UnsupportedOSPlatform("windows")]
        public static void Write(string path)
        {
            File.WriteAllText(path, "#!/bin/sh\nfor a in \"$@\"; do printf '%s\\n' \"$a\"; done > \"$0.args.part\" && mv \"$0.args.part\" \"$0.args\"\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public static async Task<string[]> ArgumentsAsync(string path, TimeSpan patience)
        {
            long started = Stopwatch.GetTimestamp();
            while (!File.Exists(path + ".args"))
            {
                if (Stopwatch.GetElapsedTime(started) > patience) throw new TimeoutException($"aesx was not started within {patience.TotalSeconds:F0} s");
                await Task.Delay(100);
            }
            return await File.ReadAllLinesAsync(path + ".args");
        }
    }

    /// <summary>
    /// Plays <c>osascript</c>: keeps the script and the arguments of every call, and prints the
    /// answer prepared for that call, or fails the way it was told to.
    /// </summary>
    private sealed class StandIn : IDisposable
    {
        private readonly TempDir _dir = new();

        [UnsupportedOSPlatform("windows")]
        public StandIn()
        {
            Path = _dir.File("osascript");
            File.WriteAllText(Path, """
                #!/bin/sh
                dir=$(dirname "$0")
                n=1; while [ -e "$dir/script.$n" ]; do n=$((n + 1)); done
                cat > "$dir/script.$n"
                i=0; for a in "$@"; do printf '%s' "$a" > "$dir/argument.$n.$i"; i=$((i + 1)); done
                if [ -f "$dir/error.$n" ]; then cat "$dir/error.$n" >&2; exit 1; fi
                if [ -f "$dir/answer.$n" ]; then cat "$dir/answer.$n"; fi
                exit 0

                """.ReplaceLineEndings("\n"));
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public string Path { get; }

        public string Directory => _dir.Path;

        public int Calls => System.IO.Directory.GetFiles(_dir.Path, "script.*").Length;

        /// <summary>What the calls print, the first for the first call and so on.</summary>
        public void Answers(params string[] printed)
        {
            for (int i = 0; i < printed.Length; i++) Answers(i + 1, printed[i]);
        }

        public void Answers(int call, string printed) => File.WriteAllText(_dir.File($"answer.{call}"), printed);

        public void Fails(int call, string complaint) => File.WriteAllText(_dir.File($"error.{call}"), complaint);

        public string Script(int call) => File.ReadAllText(_dir.File($"script.{call}"));

        public string[] Arguments(int call)
            => Enumerable.Range(0, System.IO.Directory.GetFiles(_dir.Path, $"argument.{call}.*").Length).Select(i => File.ReadAllText(_dir.File($"argument.{call}.{i}"))).ToArray();

        public void Dispose() => _dir.Dispose();
    }

    private sealed record ToolResult(int ExitCode, string Output, string Error)
    {
        public override string ToString() => $"exit {ExitCode}\n--- stdout ---\n{Output}\n--- stderr ---\n{Error}";
    }

    /// <summary>What a test needs from a Mac: its own tools, and a hand on the dialogs aesx shows.</summary>
    private static class Mac
    {
        public const string LaunchServices = "/System/Library/Frameworks/CoreServices.framework/Versions/A/Frameworks/LaunchServices.framework/Versions/A/Support/lsregister";

        // Finds the dialog by what it says, fills in its field if there is something to type, and
        // presses a button. System Events sees the dialog as a window of the osascript that shows
        // it. This script runs in an osascript too, and that one is left out: asked about its own
        // windows while it waits for the answer, it would wait for itself.
        private const string Operate = """
            on run argv
                set typed to item 1 of argv
                set pressed to item 2 of argv
                set wanted to items 3 thru -1 of argv
                set myself to (do shell script "echo $PPID") as integer
                tell application "System Events"
                    repeat with shower in (every process whose name is "osascript" and unix id is not myself)
                        repeat with shown in (every window of shower)
                            set said to ""
                            repeat with line_ in (every static text of shown)
                                set said to said & (value of line_ as text) & linefeed
                            end repeat
                            set matches to true
                            considering case
                                repeat with part in wanted
                                    if said does not contain (part as text) then set matches to false
                                end repeat
                            end considering
                            if matches then
                                if typed is not "" then set value of text field 1 of shown to typed
                                click button pressed of shown
                                return "answered"
                            end if
                        end repeat
                    end repeat
                end tell
                return "nothing there"
            end run
            """;

        /// <summary>A developer who uses aesx has the real entries, and launching a test droplet would claim their .aesx files for it.</summary>
        public static bool IsRegisteredForReal()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Directory.Exists(Path.Combine(home, "Applications", "aesx.app")) || Directory.Exists(Path.Combine(home, "Library", "Services", "Encrypt with aesx.workflow"));
        }

        /// <summary>
        /// Waits for a dialog of aesx that says all of <paramref name="saying"/>, types
        /// <paramref name="typed"/> into it (if not null) and presses <paramref name="button"/>.
        /// </summary>
        /// <param name="saying">What the dialog has to say, every piece of it.</param>
        /// <param name="typed">What to put into its field, or null to leave it alone.</param>
        /// <param name="button">The button to press.</param>
        /// <param name="patience">How long to wait for the dialog.</param>
        /// <param name="asker">The aesx that is expected to ask, if the test started it.</param>
        /// <returns>
        /// False if this Mac cannot show a dialog (nobody is logged in at its screen) or does not let
        /// a test operate one, which is fine anywhere but on the CI runners.
        /// </returns>
        public static async Task<bool> AnswerAsync(string[] saying, string? typed, string button, TimeSpan patience, Process? asker = null)
        {
            bool required = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
            long started = Stopwatch.GetTimestamp();
            string last = "";
            int refusals = 0;
            while (Stopwatch.GetElapsedTime(started) < patience)
            {
                ToolResult result = await RunAsync("/usr/bin/osascript", Operate, ["-", typed ?? "", button, .. saying]);
                if (result.ExitCode == 0 && result.Output.Trim() == "answered") return true;
                last = result.ToString();

                // Not allowed assistive access (-1719, -25211), or not allowed to talk to System
                // Events at all (-1743). But -1719 is also what the script gets for a dialog that
                // went away while it was looking, which the one answered a moment ago does. That
                // happens once; a Mac that does not allow this says so at every look.
                bool refused = result.Error.Contains("-1719", StringComparison.Ordinal) || result.Error.Contains("-25211", StringComparison.Ordinal) || result.Error.Contains("-1743", StringComparison.Ordinal);
                refusals = refused ? refusals + 1 : 0;
                if (refusals == 5)
                {
                    Assert.False(required, $"the runner does not let the test operate the dialog:\n{last}");
                    return false;
                }
                if (asker is { HasExited: true })
                {
                    Assert.False(required, $"aesx ended with code {asker.ExitCode} without asking");
                    return false;
                }
                await Task.Delay(250);
            }
            throw new TimeoutException($"aesx did not show a dialog saying '{string.Join("' and '", saying)}' within {patience.TotalSeconds:F0} s; the last look gave:\n{last}");
        }

        public static async Task<ToolResult> RunAsync(string program, string? input, params string[] arguments)
        {
            var start = new ProcessStartInfo(program)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (input is not null) await process.StandardInput.WriteAsync(input.ReplaceLineEndings("\n"));
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"{program} did not finish within four minutes");
            }
            return new ToolResult(process.ExitCode, await output, await error);
        }
    }

    /// <summary>Reads an XML property list into dictionaries, lists, strings, numbers and booleans.</summary>
    private static class Plist
    {
        public static object Read(string path)
        {
            // The document names Apple's DTD; it is neither fetched nor needed.
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using XmlReader reader = XmlReader.Create(path, settings);
            XElement root = XDocument.Load(reader).Root!;
            Assert.Equal("plist", root.Name.LocalName);
            return Convert(root.Elements().Single());
        }

        /// <summary>Follows keys and indexes down from a node.</summary>
        public static object At(object node, params object[] path)
        {
            foreach (object step in path)
            {
                node = step switch
                {
                    string key => ((Dictionary<string, object>)node)[key],
                    int index => ((List<object>)node)[index],
                    _ => throw new ArgumentException($"neither a key nor an index: {step}", nameof(path)),
                };
            }
            return node;
        }

        private static object Convert(XElement element)
        {
            switch (element.Name.LocalName)
            {
                case "dict":
                    {
                        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
                        XElement[] children = element.Elements().ToArray();
                        Assert.True(children.Length % 2 == 0, "a dictionary is keys and values in turn");
                        for (int i = 0; i < children.Length; i += 2)
                        {
                            Assert.Equal("key", children[i].Name.LocalName);
                            entries.Add(children[i].Value, Convert(children[i + 1]));
                        }
                        return entries;
                    }
                case "array":
                    return element.Elements().Select(Convert).ToList();
                case "string":
                    return element.Value;
                case "integer":
                    return long.Parse(element.Value, CultureInfo.InvariantCulture);
                case "true":
                    return true;
                case "false":
                    return false;
                default:
                    throw new InvalidDataException($"unexpected <{element.Name.LocalName}> in a property list");
            }
        }
    }
}
