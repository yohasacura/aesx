// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Diagnostics;

namespace Aesx.Cli;

/// <summary>
/// Counts the bytes read from a file and keeps a one-line percentage on the terminal, so a
/// multi-gigabyte job is visibly alive instead of looking hung. Only used when standard error
/// is a terminal and the file is large enough for the wait to be noticeable.
/// </summary>
internal sealed class ProgressStream(Stream inner, long total) : Stream
{
    private const long MinimumSize = 64L << 20;
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private long _done;
    private long _lastReport = Stopwatch.GetTimestamp();
    private bool _shown;

    public static Stream WrapIfWorthwhile(Stream inner, bool quiet)
    {
        if (quiet || Console.IsErrorRedirected) return inner;
        long length;
        try
        {
            length = inner.Length;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            return inner; // a pipe or a device: no known size, no percentage
        }
        return length < MinimumSize ? inner : new ProgressStream(inner, length);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => total;
    public override long Position
    {
        get => _done;
        set => throw new NotSupportedException();
    }

    public override int Read(Span<byte> buffer)
    {
        int n = inner.Read(buffer);
        Advance(n);
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = inner.Read(buffer, offset, count);
        Advance(n);
        return n;
    }

    private void Advance(int n)
    {
        _done += n;
        if (Stopwatch.GetElapsedTime(_lastReport) < Interval) return;
        _lastReport = Stopwatch.GetTimestamp();
        int percent = total <= 0 ? 100 : (int)Math.Min(100, _done * 100 / total);
        Console.Error.Write($"\r  {percent,3}%  {_done >> 20:N0} / {total >> 20:N0} MiB");
        _shown = true;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _shown)
        {
            Console.Error.Write("\r" + new string(' ', 40) + "\r");
            _shown = false;
        }
        // The caller owns and disposes the underlying file.
        base.Dispose(disposing);
    }
}
