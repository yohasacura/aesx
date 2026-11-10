// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Aesx.Cli;

internal static class Program
{
    public const int ExitSuccess = 0;
    public const int ExitFailure = 1;
    public const int ExitUsage = 2;
    public const int ExitInterrupted = 130;

    private const string StandardStream = CommandLine.StandardStream;

    /// <summary>Set when an Explorer menu entry started this run: the outcome is then shown in a window, and nothing waits for a key.</summary>
    private static bool s_startedFromExplorerMenu;

    /// <summary>What happens to one input.</summary>
    private enum Operation
    {
        Encrypt,
        Decrypt,
        Verify,
    }

    /// <summary>Version of this build, from the assembly version (set through &lt;Version&gt; or -p:Version).</summary>
    public static string Version => (typeof(Program).Assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(3);

    private static int Main(string[] args)
    {
        UseUtf8ForRedirectedOutput();
        int code;
        try
        {
            code = Run(args);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine("Run 'aesx --help' for usage.");
            code = ExitUsage;
        }
        catch (PromptCancelledException)
        {
            return ExitInterrupted;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("error: interrupted; the unfinished output was removed");
            code = ExitInterrupted;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            if (Environment.GetEnvironmentVariable("AESX_DEBUG") == "1") Console.Error.WriteLine(ex);
            code = ExitFailure;
        }
        if (!s_startedFromExplorerMenu) ConsoleSupport.PauseIfLaunchedFromExplorer();
        return code;
    }

    private static int Run(string[] args)
    {
        Options o = CommandLine.Parse(args);
        switch (o.Command)
        {
            case Command.Help:
                CommandLine.PrintUsage(Console.Out);
                return ExitSuccess;
            case Command.Version:
                Console.WriteLine($"aesx {Version}");
                return ExitSuccess;
            case Command.SelfTest:
                return SelfTest.Run();
            case Command.Register or Command.Unregister:
                return ShellIntegration.Run(o);
            case Command.Explorer:
                return RunForExplorer();
            default:
                return RunOn(o);
        }
    }

    /// <summary>
    /// Started by Windows because somebody chose an aesx entry in an Explorer menu: Explorer
    /// says which entry and what was selected, and from there on it is an ordinary run.
    /// </summary>
    private static int RunForExplorer()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (ExplorerSelection.Receive(TimeSpan.FromSeconds(30)) is not { } delivery)
            return ExitFailure; // started as if for a menu entry, but nobody asked for anything
        if (delivery.Paths.Count == 0)
            throw new InvalidOperationException("none of the selected items is a file or a folder");

        var options = new Options
        {
            FromExplorer = true,
            Command = delivery.Verb switch
            {
                ShellRegistration.EncryptVerb => Command.Encrypt,
                ShellRegistration.DecryptVerb => Command.Decrypt,
                _ => Command.Auto,
            },
        };
        options.Inputs.AddRange(delivery.Paths);

        // Windows may have started this process with or without a console window, and nobody
        // watches one after a click in a menu: whatever needs saying is said in a window at the
        // end. When all went well there is nothing to say, and the new files speak for themselves.
        s_startedFromExplorerMenu = true;
        var said = new List<string>();
        Notices.Collector = said;
        int code;
        try
        {
            code = RunOn(options);
        }
        catch (OperationCanceledException)
        {
            said.Add("Interrupted; the unfinished output was removed.");
            code = ExitInterrupted;
        }
        catch (Exception ex) when (ex is not PromptCancelledException)
        {
            Notices.Say($"error: {ex.Message}");
            code = ExitFailure;
        }
        finally
        {
            Notices.Collector = null;
        }
        if (said.Count > 0) PasswordDialog.Tell(said, failed: code != ExitSuccess);
        return code;
    }

    private static int RunOn(Options o)
    {
        List<string> inputs = CommandLine.ExpandWildcards(o.Inputs);
        if (inputs.Count == 0)
        {
            CommandLine.PrintUsage(Console.Error);
            return ExitUsage;
        }
        CommandLine.CheckCombinations(o, inputs);
        if (o.Command == Command.Info) return ShowInfo(inputs);
        if (!AesxFormat.IsSupported)
            throw new PlatformNotSupportedException("AES-GCM is not available on this system");

        var jobs = inputs.ConvertAll(input => (Input: input, Operation: o.Command switch
        {
            Command.Encrypt => Operation.Encrypt,
            Command.Decrypt => Operation.Decrypt,
            Command.Verify => Operation.Verify,
            _ => Directory.Exists(input) || !input.EndsWith(AesxFormat.FileExtension, StringComparison.OrdinalIgnoreCase)
                ? Operation.Encrypt
                : Operation.Decrypt,
        }));

        bool toStandardOutput = o.Output == StandardStream;
        bool fromStandardInput = inputs[0] == StandardStream;
        bool anyEncrypt = jobs.Exists(j => j.Operation == Operation.Encrypt);
        if (toStandardOutput && anyEncrypt && !Console.IsOutputRedirected && !o.Force)
            throw new UsageException("refusing to write encrypted data to the terminal; redirect standard output or use --force");
        if (anyEncrypt && o.Iterations < 100_000)
            Notices.Say($"warning: {o.Iterations} PBKDF2 iterations is low (default: {AesxFormat.DefaultIterations})");
        if (!anyEncrypt && o.IterationsSpecified)
            Notices.Say("warning: --iterations only applies when encrypting; ignored (the count is stored in the file)");

        bool interactive = o.Password is null
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PasswordInput.EnvironmentVariable))
            && !Console.IsInputRedirected;
        byte[] password = PasswordInput.Obtain(o, confirm: anyEncrypt, standardInputIsData: fromStandardInput, Describe(jobs));

        // Status lines go to standard output, unless that is where the data goes.
        TextWriter status = toStandardOutput || o.Quiet ? TextWriter.Null : Console.Out;
        if (toStandardOutput && !o.Quiet) status = Console.Error;

        // From here on an interrupt (Ctrl+C, closed window, SIGTERM) is turned into a
        // cancellation so the output in progress is cleaned up before exiting.
        using var interrupts = new InterruptGuard();
        int failures = 0;
        try
        {
            interrupts.Armed = true;
            foreach ((string input, Operation operation) in jobs)
            {
                try
                {
                    switch (operation)
                    {
                        case Operation.Encrypt:
                            EncryptInput(input, o, password, status, interrupts.Token);
                            break;
                        case Operation.Decrypt:
                            DecryptInput(input, o, password, status, interrupts.Token);
                            break;
                        default:
                            VerifyInput(input, o, password, status, interrupts.Token);
                            break;
                    }
                }
                catch (Exception ex) when (jobs.Count > 1 && ex is not OperationCanceledException)
                {
                    failures++;
                    Notices.Say($"error: {TextSafety.Display(input)}: {ex.Message}");
                }
            }
        }
        finally
        {
            interrupts.Armed = false;
            interrupts.MarkFinished();
            CryptographicOperations.ZeroMemory(password);
        }

        if (anyEncrypt && failures == 0 && interactive)
            status.WriteLine("Keep the password safe: an encrypted file cannot be recovered without it.");
        return failures == 0 ? ExitSuccess : ExitFailure;
    }

    /// <summary>A few words for the password window: what the password is about to be used for.</summary>
    private static string Describe(List<(string Input, Operation Operation)> jobs)
    {
        if (jobs.Count == 1)
        {
            string name = jobs[0].Input == StandardStream ? "standard input" : Path.GetFileName(Path.TrimEndingDirectorySeparator(jobs[0].Input));
            return $"{jobs[0].Operation switch { Operation.Encrypt => "Encrypt", Operation.Decrypt => "Decrypt", _ => "Check" }} {TextSafety.Display(name)}";
        }
        bool encrypt = jobs.Exists(j => j.Operation == Operation.Encrypt);
        bool decrypt = jobs.Exists(j => j.Operation == Operation.Decrypt);
        string verb = (encrypt, decrypt) switch
        {
            (true, true) => "Encrypt and decrypt",
            (true, false) => "Encrypt",
            (false, true) => "Decrypt",
            _ => "Check",
        };
        return $"{verb} {jobs.Count} items";
    }

    private static void EncryptInput(string input, Options o, byte[] password, TextWriter status, CancellationToken cancellationToken)
    {
        bool fromStandardInput = input == StandardStream;
        string shownInput = fromStandardInput ? "standard input" : TextSafety.Display(input);
        string fullInput = fromStandardInput ? "" : FullPathOrFail(input, "input");
        bool folder = !fromStandardInput && Directory.Exists(fullInput);
        if (!fromStandardInput && !folder && !File.Exists(fullInput))
            throw new FileNotFoundException($"input file not found: {shownInput}");
        if (folder && o.DeleteInput)
            throw new InvalidOperationException($"--delete does not remove folders: '{shownInput}' was left alone and not encrypted (run it again without --delete)");
        if (!folder && o.Command == Command.Encrypt && input.EndsWith(AesxFormat.FileExtension, StringComparison.OrdinalIgnoreCase))
            Notices.Say($"warning: {shownInput} already has the {AesxFormat.FileExtension} extension and will be encrypted a second time");

        FolderSummary contents = default;
        void Write(Stream sink, IReadOnlyCollection<string> ownFiles)
        {
            if (folder)
            {
                string top = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullInput));
                using var progress = new FolderProgress(o.Quiet);
                using var encryptor = new AesxEncryptStream(sink, password, o.Iterations, leaveOpen: true, cancellationToken);
                contents = FolderArchive.Write(
                    fullInput,
                    top.Length == 0 ? "root" : top, // a whole drive has no name of its own
                    encryptor,
                    ownFiles,
                    message =>
                    {
                        progress.Clear();
                        Notices.Say($"warning: {message}");
                    },
                    progress.Report,
                    cancellationToken);
                encryptor.Complete();
            }
            else
            {
                using Stream source = fromStandardInput ? Console.OpenStandardInput() : OpenForReading(fullInput);
                using Stream measured = ProgressStream.WrapIfWorthwhile(source, o.Quiet);
                AesxFormat.Encrypt(measured, sink, password, o.Iterations, cancellationToken);
            }
        }

        string what = folder ? "folder " : "";
        if (o.Output == StandardStream)
        {
            status.WriteLine($"Encrypting {what}{shownInput} -> standard output");
            long started = Stopwatch.GetTimestamp();
            using (Stream stdout = Console.OpenStandardOutput())
            {
                Write(stdout, []);
                stdout.Flush();
            }
            status.WriteLine($"Done{Counted(folder, contents)} in {Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s");
            return;
        }

        string output = fromStandardInput
            ? OutputFileFor(o.Output!)
            : OutputPathFor(o.Output, () => folder ? DefaultFolderOutput(input, fullInput) : input + AesxFormat.FileExtension);
        string shownOutput = TextSafety.Display(output);
        string fullOutput = FullPathOrFail(output, "output");
        if (Directory.Exists(fullOutput))
            throw new InvalidOperationException($"output path is a directory: {shownOutput}");
        if (!fromStandardInput && !folder && SameFile(fullInput, fullOutput))
            throw new InvalidOperationException("input and output are the same file");
        if (File.Exists(fullOutput) && !o.Force)
            throw new InvalidOperationException($"output file already exists: {shownOutput} (use -f to overwrite)");
        if (folder && !FolderArchive.HasExtension(fullOutput))
            Notices.Say($"note: {shownOutput} does not end in {FolderArchive.Extension}, so decrypting it will give a tar archive rather than the folder");

        status.WriteLine($"Encrypting {what}{shownInput} -> {shownOutput}");
        double seconds = WriteThroughTemporaryFile(fullOutput, ownerOnly: false, o.Force, Write);

        bool verified = false;
        if (o.DeleteInput)
        {
            // The original is about to disappear: make sure what replaces it really decrypts.
            try
            {
                using FileStream check = OpenForReading(fullOutput);
                AesxFormat.Decrypt(check, Stream.Null, password, cancellationToken);
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidDataException or IOException)
            {
                throw new IOException($"{shownOutput} was written but failed verification ({ex.Message}); {shownInput} was kept", ex);
            }
            verified = true;
            DeleteInput(fullInput, shownOutput);
        }

        status.WriteLine($"Done: {new FileInfo(fullOutput).Length:N0} bytes{Counted(folder, contents)} in {seconds:F1} s{(verified ? ", verified" : "")}{(o.DeleteInput ? ", input deleted" : "")}");
    }

    private static void DecryptInput(string input, Options o, byte[] password, TextWriter status, CancellationToken cancellationToken)
    {
        bool fromStandardInput = input == StandardStream;
        string shownInput = fromStandardInput ? "standard input" : TextSafety.Display(input);
        string fullInput = fromStandardInput ? "" : FullPathOrFail(input, "input");
        if (!fromStandardInput) RequireFile(fullInput, shownInput, "only files can be decrypted");

        bool toStandardOutput = o.Output == StandardStream;
        if (!fromStandardInput && !toStandardOutput && FolderArchive.HasExtension(fullInput))
        {
            DecryptFolder(input, fullInput, o, password, status, cancellationToken);
            return;
        }

        void Write(Stream sink, IReadOnlyCollection<string> ownFiles)
        {
            using Stream source = fromStandardInput ? Console.OpenStandardInput() : OpenForReading(fullInput);
            using Stream measured = ProgressStream.WrapIfWorthwhile(source, o.Quiet);
            AesxFormat.Decrypt(measured, sink, password, cancellationToken);
        }

        if (toStandardOutput)
        {
            status.WriteLine($"Decrypting {shownInput} -> standard output");
            long started = Stopwatch.GetTimestamp();
            using (Stream stdout = Console.OpenStandardOutput())
            {
                Write(stdout, []);
                stdout.Flush();
            }
            status.WriteLine($"Done in {Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s");
            return;
        }

        string output = fromStandardInput ? OutputFileFor(o.Output!) : OutputPathFor(o.Output, () => DefaultDecryptedName(input));
        string shownOutput = TextSafety.Display(output);
        string fullOutput = FullPathOrFail(output, "output");
        if (Directory.Exists(fullOutput))
            throw new InvalidOperationException($"output path is a directory: {shownOutput}");
        if (!fromStandardInput && SameFile(fullInput, fullOutput))
            throw new InvalidOperationException("input and output are the same file");
        if (File.Exists(fullOutput) && !o.Force)
            throw new InvalidOperationException($"output file already exists: {shownOutput} (use -f to overwrite)");

        status.WriteLine($"Decrypting {shownInput} -> {shownOutput}");
        double seconds = WriteThroughTemporaryFile(fullOutput, ownerOnly: true, o.Force, Write);
        if (o.DeleteInput) DeleteInput(fullInput, shownOutput);
        status.WriteLine($"Done: {new FileInfo(fullOutput).Length:N0} bytes in {seconds:F1} s{(o.DeleteInput ? ", input deleted" : "")}");
    }

    /// <summary>
    /// Restores an encrypted folder the way a file is written: into a uniquely named, private
    /// folder next to the destination that takes the real name only once the whole archive has
    /// been read and verified. A wrong password, a damaged file, a full disk or an interrupt
    /// leaves nothing behind, and an existing folder is never touched.
    /// </summary>
    private static void DecryptFolder(string input, string fullInput, Options o, byte[] password, TextWriter status, CancellationToken cancellationToken)
    {
        string shownInput = TextSafety.Display(input);
        string target = Path.TrimEndingDirectorySeparator(OutputPathFor(o.Output, () => input[..^FolderArchive.Extension.Length]));
        string shownTarget = TextSafety.Display(target);
        string fullTarget = FullPathOrFail(target, "output");
        if (Directory.Exists(fullTarget) || File.Exists(fullTarget))
            throw new InvalidOperationException($"output folder already exists: {shownTarget} (move it away or pick another place with -o; a folder is never overwritten{(o.Force ? ", not even with --force" : "")})");

        status.WriteLine($"Decrypting {shownInput} -> folder {shownTarget}");
        // Twice the random bits of a file's temporary name: nothing can demand that a folder be
        // created new, so the name itself has to be beyond guessing.
        string temp = $"{fullTarget}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}.part";
        long started = Stopwatch.GetTimestamp();
        FolderSummary contents;
        try
        {
            if (Directory.Exists(temp) || File.Exists(temp))
                throw new IOException($"a temporary name is already taken: {TextSafety.Display(temp)}");
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(temp);
            else Directory.CreateDirectory(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            using (FileStream file = OpenForReading(fullInput))
            using (Stream measured = ProgressStream.WrapIfWorthwhile(file, o.Quiet))
            using (var decryptor = new AesxDecryptStream(measured, password, leaveOpen: true, cancellationToken))
            {
                contents = FolderArchive.Extract(decryptor, temp, progress: null, cancellationToken);
            }

            try
            {
                Directory.Move(temp, fullTarget);
            }
            catch (IOException) when (Directory.Exists(fullTarget) || File.Exists(fullTarget))
            {
                throw new InvalidOperationException($"output folder appeared while working: {shownTarget}");
            }
        }
        catch
        {
            TryDeleteFolder(temp);
            throw;
        }

        if (o.DeleteInput) DeleteInput(fullInput, shownTarget);
        status.WriteLine($"Done: {contents.Files:N0} file{(contents.Files == 1 ? "" : "s")}, {contents.Bytes:N0} bytes in {Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s{(o.DeleteInput ? ", input deleted" : "")}");
    }

    /// <summary>Decrypts and throws the plaintext away; for an encrypted folder the archive inside is walked as well.</summary>
    private static void VerifyInput(string input, Options o, byte[] password, TextWriter status, CancellationToken cancellationToken)
    {
        bool fromStandardInput = input == StandardStream;
        string shownInput = fromStandardInput ? "standard input" : TextSafety.Display(input);
        string fullInput = fromStandardInput ? "" : FullPathOrFail(input, "input");
        if (!fromStandardInput) RequireFile(fullInput, shownInput, "only files can be checked");

        status.WriteLine($"Checking {shownInput}");
        long started = Stopwatch.GetTimestamp();
        string found;
        using (Stream source = fromStandardInput ? Console.OpenStandardInput() : OpenForReading(fullInput))
        using (Stream measured = ProgressStream.WrapIfWorthwhile(source, o.Quiet))
        {
            if (!fromStandardInput && FolderArchive.HasExtension(fullInput))
            {
                using var decryptor = new AesxDecryptStream(measured, password, leaveOpen: true, cancellationToken);
                FolderSummary contents = FolderArchive.Extract(decryptor, destination: null, progress: null, cancellationToken);
                found = $"a folder with {contents.Files:N0} file{(contents.Files == 1 ? "" : "s")}, {contents.Bytes:N0} bytes";
            }
            else
            {
                using var discard = new CountingStream();
                AesxFormat.Decrypt(measured, discard, password, cancellationToken);
                found = $"{discard.Count:N0} bytes";
            }
        }
        status.WriteLine($"OK: {found}, in {Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s");
    }

    /// <summary>
    /// Prints what the headers say. Nothing here needs or proves a password: the header is plain
    /// text, and only decrypting (or <c>verify</c>) shows that it belongs to the rest of the file.
    /// </summary>
    private static int ShowInfo(List<string> inputs)
    {
        int failures = 0;
        foreach (string input in inputs)
        {
            try
            {
                string shown = TextSafety.Display(input);
                string full = FullPathOrFail(input, "input");
                RequireFile(full, shown, "info reads AESX files");

                AesxHeader header;
                long length;
                using (FileStream file = OpenForReading(full))
                {
                    header = AesxFormat.ReadHeader(file);
                    length = file.Length;
                }
                if (!AesxFormat.TryGetLayout(length, out long chunks, out long plaintextLength))
                    throw new InvalidDataException("file is truncated or corrupted");

                Console.WriteLine(shown);
                Console.WriteLine($"  format      AESX version {header.Version} (AES-256-GCM, key from PBKDF2-HMAC-SHA256)");
                Console.WriteLine($"  iterations  {header.Iterations:N0}");
                Console.WriteLine($"  size        {length:N0} bytes in {chunks:N0} chunk{(chunks == 1 ? "" : "s")}");
                Console.WriteLine($"  decrypted   {plaintextLength:N0} bytes{(FolderArchive.HasExtension(full) ? ", a folder archive going by the name" : "")}");
            }
            catch (Exception ex) when (inputs.Count > 1 && ex is not OperationCanceledException)
            {
                failures++;
                Console.Error.WriteLine($"error: {TextSafety.Display(input)}: {ex.Message}");
            }
        }
        if (failures < inputs.Count)
            Console.WriteLine("This is what the header says; 'aesx verify' checks the whole file against a password.");
        return failures == 0 ? ExitSuccess : ExitFailure;
    }

    /// <summary>
    /// Writes to a uniquely named temporary file created exclusively (so a planted symbolic link
    /// cannot redirect the write), then renames it into place. A wrong password, a damaged
    /// input, a full disk, an interrupt or a crash therefore never leaves a partial output.
    /// </summary>
    /// <returns>The seconds the write took.</returns>
    private static double WriteThroughTemporaryFile(string output, bool ownerOnly, bool force, Action<Stream, IReadOnlyCollection<string>> write)
    {
        string temp = $"{output}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.part";
        long started = Stopwatch.GetTimestamp();
        try
        {
            using (FileStream fout = CreateExclusively(temp, ownerOnly))
            {
                // A folder that contains its own output must not swallow it.
                write(fout, [output, temp]);
                fout.Flush(flushToDisk: true);
            }

            // Without --force the rename itself refuses to replace a file, so one that appeared
            // while we were working is as safe as one that was there from the start.
            try
            {
                File.Move(temp, output, overwrite: force);
            }
            catch (IOException) when (!force && File.Exists(output))
            {
                throw new InvalidOperationException($"output file appeared while working: {TextSafety.Display(output)} (use -f to overwrite)");
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        return Stopwatch.GetElapsedTime(started).TotalSeconds;
    }

    private static void DeleteInput(string fullInput, string shownOutput)
    {
        try
        {
            File.Delete(fullInput);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"{shownOutput} was written, but the input could not be deleted: {ex.Message}", ex);
        }
    }

    private static void RequireFile(string fullPath, string shown, string why)
    {
        if (Directory.Exists(fullPath))
            throw new InvalidOperationException($"'{shown}' is a directory; {why}");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"input file not found: {shown}");
    }

    private static string Counted(bool folder, FolderSummary contents)
        => folder ? $" ({contents.Files:N0} file{(contents.Files == 1 ? "" : "s")}, {contents.Bytes:N0} bytes{(contents.Skipped > 0 ? $", {contents.Skipped:N0} not included in full" : "")})" : "";

    /// <summary>The requested path, or the default name; a requested directory receives the default name inside it.</summary>
    private static string OutputPathFor(string? requested, Func<string> defaultPath)
    {
        if (requested is null) return defaultPath();
        if (Directory.Exists(requested)) return Path.Combine(requested, Path.GetFileName(defaultPath()));
        return requested;
    }

    /// <summary>Data from standard input has no name to derive one from, so <c>-o</c> has to be the file itself.</summary>
    private static string OutputFileFor(string requested)
    {
        if (Directory.Exists(requested))
            throw new UsageException($"data read from standard input has no name, so -o must name the output file, not a directory: {TextSafety.Display(requested)}");
        return requested;
    }

    private static string DefaultDecryptedName(string input)
    {
        string ext = AesxFormat.FileExtension;
        if (input.Length > ext.Length && input.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            return input[..^ext.Length];
        throw new UsageException($"cannot derive an output name for '{TextSafety.Display(input)}' (no {ext} extension); use -o <file>");
    }

    /// <summary>
    /// <c>photos</c> and <c>photos/</c> become <c>photos.folder.aesx</c> next to the folder. A
    /// path that does not end in a name (<c>.</c>, <c>..</c>) uses the folder's real one.
    /// </summary>
    private static string DefaultFolderOutput(string input, string fullInput)
    {
        string typed = Path.TrimEndingDirectorySeparator(input);
        string name = Path.GetFileName(typed);
        if (name.Length > 0 && name is not ("." or "..")) return typed + FolderArchive.Extension;

        string full = Path.TrimEndingDirectorySeparator(fullInput);
        if (Path.GetFileName(full).Length == 0)
            throw new UsageException($"cannot derive an output name for '{TextSafety.Display(input)}' (it has no name of its own); use -o <file>");
        return full + FolderArchive.Extension;
    }

    private static string FullPathOrFail(string path, string role)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            throw new InvalidOperationException($"invalid {role} path '{TextSafety.Display(path)}': {ex.Message}", ex);
        }

        // Windows forbids a handful of characters in names; the file system would only say so
        // after the work is done, with a message that does not name the culprit.
        if (OperatingSystem.IsWindows()
            && (Path.GetFileName(full).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || full.IndexOfAny(Path.GetInvalidPathChars()) >= 0))
        {
            throw new InvalidOperationException($"invalid {role} path '{TextSafety.Display(path)}': Windows does not allow the characters < > : \" | ? * or control characters in file names");
        }
        return full;
    }

    /// <summary>True when both names reach the same existing file, following symbolic links.</summary>
    private static bool SameFile(string input, string output)
    {
        if (!File.Exists(output)) return false;
        StringComparison comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return string.Equals(FinalPath(input), FinalPath(output), comparison);
    }

    private static string FinalPath(string fullPath)
    {
        try
        {
            return File.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName ?? fullPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return fullPath;
        }
    }

    /// <summary>Unbuffered: the format reads whole 1 MiB chunks, so an extra copy of plaintext in a stream buffer would only linger.</summary>
    private static FileStream OpenForReading(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.SequentialScan);

    /// <summary>Creates a brand-new file (never follows an existing path or link); decrypted output is readable by its owner only on Unix.</summary>
    private static FileStream CreateExclusively(string path, bool ownerOnly)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 0,
        };
        if (ownerOnly && !OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Logs and pipes get UTF-8 (the Unix default); on Windows the console code page would turn non-Latin file names into question marks. A live console is left alone.</summary>
    private static void UseUtf8ForRedirectedOutput()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (Console.IsOutputRedirected) Console.OutputEncoding = utf8;
        if (Console.IsErrorRedirected && !Console.IsOutputRedirected) Console.OutputEncoding = utf8;
    }

    /// <summary>Swallows what is written to it and remembers how much that was.</summary>
    private sealed class CountingStream : Stream
    {
        public long Count { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position
        {
            get => Count;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Count += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Count += buffer.Length;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
