namespace SimpleBin.Helpers
{
    internal enum VolumeEvent
    {
        Arrival,
        RemovalRequested,
        RemovalPending,
        RemovalCompleted,
        RemovalCancelled,
        TopologyChanged,
        ShellDriveChanged
    }

    // Records Windows volume events without opening directories, files, or
    // handles to mounted volumes. One bit per drive letter.
    internal sealed class VolumeChangeTracker
    {
        internal static readonly TimeSpan RemovalGracePeriod = TimeSpan.FromSeconds(15);

        private readonly DateTime[] _pendingByLetter = new DateTime[26];
        private DateTime _unknownPendingUntil;
        private long _version;

        internal long Version => _version;

        internal void Observe(VolumeEvent change, uint letters, DateTime utcNow)
        {
            ++_version;
            switch (change)
            {
                case VolumeEvent.RemovalRequested:
                case VolumeEvent.RemovalPending:
                    DateTime expires = utcNow + RemovalGracePeriod;
                    if (letters == 0)
                    {
                        _unknownPendingUntil = expires;
                    }
                    else
                    {
                        for (int i = 0; i < 26; ++i)
                            if ((letters & (1u << i)) != 0)
                                _pendingByLetter[i] = expires;
                    }
                    break;

                case VolumeEvent.Arrival:
                case VolumeEvent.RemovalCompleted:
                case VolumeEvent.RemovalCancelled:
                    if (letters == 0)
                    {
                        _unknownPendingUntil = DateTime.MinValue;
                    }
                    else
                    {
                        for (int i = 0; i < 26; ++i)
                            if ((letters & (1u << i)) != 0)
                                _pendingByLetter[i] = DateTime.MinValue;
                    }
                    break;

                case VolumeEvent.TopologyChanged:
                case VolumeEvent.ShellDriveChanged:
                    break;
            }
        }

        internal void ReconcileAssignedLetters(uint assignedLetters)
        {
            for (int i = 0; i < 26; ++i)
            {
                if (_pendingByLetter[i] != DateTime.MinValue &&
                    (assignedLetters & (1u << i)) == 0)
                {
                    _pendingByLetter[i] = DateTime.MinValue;
                    ++_version;
                }
            }
        }

        // This is evaluated only in direct response to a Windows event, a
        // bounded one-shot timer, or user interaction. No periodic polling.
        internal int RemainingHoldMilliseconds(DateTime utcNow)
        {
            DateTime latest = _unknownPendingUntil;
            for (int i = 0; i < _pendingByLetter.Length; ++i)
                if (_pendingByLetter[i] > latest)
                    latest = _pendingByLetter[i];

            if (latest <= utcNow) return 0;
            double millis = (latest - utcNow).TotalMilliseconds;
            return (int)Math.Clamp(Math.Ceiling(millis), 1, 60_000);
        }
    }
}
