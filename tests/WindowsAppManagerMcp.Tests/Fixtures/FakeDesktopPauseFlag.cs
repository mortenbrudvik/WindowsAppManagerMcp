namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// A fake of the pause the servers on a desktop share: an in-memory event instead of the named one, so a test
/// can hand one flag to two input sessions, as two server processes would open the same named event, without
/// touching the real pause of servers running on the machine. Not disposable: the sessions that share it do not
/// close it. A read, a write and a change of <see cref="ReadFails"/> or <see cref="WriteFails"/> each happen at once, as
/// on the named event: a session's watcher thread that reads while the test changes the flag sees it before or after the
/// change, never a read that started before a failure and ended after a reset.
/// </summary>
public sealed class FakeDesktopPauseFlag : IDesktopPauseFlag
{
    private readonly ManualResetEvent _event = new(initialState: false);
    private readonly object _gate = new();
    private Exception? _writeFails;
    private Exception? _readFails;
    private int _sets;
    private int _resets;

    /// <summary>Makes <see cref="Set"/> and <see cref="Reset"/> throw, as a flag that cannot be written would.</summary>
    public Exception? WriteFails
    {
        get { lock (_gate) { return _writeFails; } }
        set { lock (_gate) { _writeFails = value; } }
    }

    /// <summary>Makes <see cref="IsSet"/> throw, as a flag that cannot be read would.</summary>
    public Exception? ReadFails
    {
        get { lock (_gate) { return _readFails; } }
        set { lock (_gate) { _readFails = value; } }
    }

    /// <summary>How many times a session set the flag.</summary>
    public int Sets => Volatile.Read(ref _sets);

    /// <summary>How many times a session reset the flag.</summary>
    public int Resets => Volatile.Read(ref _resets);

    public bool IsSet
    {
        get
        {
            lock (_gate)
            {
                return _readFails is { } failure ? throw failure : _event.WaitOne(0);
            }
        }
    }

    public WaitHandle WaitHandle => _event;

    public void Set()
    {
        lock (_gate)
        {
            if (_writeFails is { } failure)
            {
                throw failure;
            }

            Interlocked.Increment(ref _sets);
            _event.Set();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            if (_writeFails is { } failure)
            {
                throw failure;
            }

            Interlocked.Increment(ref _resets);
            _event.Reset();
        }
    }

    /// <summary>Sets the flag as another server's hotkey would, without counting it as this test's session's write.</summary>
    public void SetByAnotherServer()
    {
        lock (_gate)
        {
            _event.Set();
        }
    }

    /// <summary>Resets the flag as another server would.</summary>
    public void ResetByAnotherServer()
    {
        lock (_gate)
        {
            _event.Reset();
        }
    }
}
