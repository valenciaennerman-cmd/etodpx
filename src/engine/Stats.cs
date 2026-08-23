// etodpx - sayaclar. Sicak yolda sadece Interlocked.Increment var.
using System;
using System.Threading;

namespace Etodpx.Engine
{
    internal sealed class Stats
    {
        private long _connections;
        private long _fakes;
        private long _splits;
        private long _hosts;
        private long _quic;
        private long _passthrough;
        private long _sendErrors;
        private long _startedAtTicks;

        public void BumpConnection() { Interlocked.Increment(ref _connections); }
        public void BumpFake() { Interlocked.Increment(ref _fakes); }
        public void BumpSplit() { Interlocked.Increment(ref _splits); }
        public void BumpHost() { Interlocked.Increment(ref _hosts); }
        public void BumpQuic() { Interlocked.Increment(ref _quic); }
        public void BumpPassthrough() { Interlocked.Increment(ref _passthrough); }
        public void BumpSendError() { Interlocked.Increment(ref _sendErrors); }

        public long Connections { get { return Interlocked.Read(ref _connections); } }
        public long Fakes { get { return Interlocked.Read(ref _fakes); } }
        public long Splits { get { return Interlocked.Read(ref _splits); } }
        public long Hosts { get { return Interlocked.Read(ref _hosts); } }
        public long Quic { get { return Interlocked.Read(ref _quic); } }
        public long Passthrough { get { return Interlocked.Read(ref _passthrough); } }
        public long SendErrors { get { return Interlocked.Read(ref _sendErrors); } }

        public void MarkStart()
        {
            Interlocked.Exchange(ref _startedAtTicks, DateTime.UtcNow.Ticks);
        }

        public TimeSpan Uptime
        {
            get
            {
                long t = Interlocked.Read(ref _startedAtTicks);
                if (t == 0) return TimeSpan.Zero;
                return DateTime.UtcNow - new DateTime(t, DateTimeKind.Utc);
            }
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _connections, 0);
            Interlocked.Exchange(ref _fakes, 0);
            Interlocked.Exchange(ref _splits, 0);
            Interlocked.Exchange(ref _hosts, 0);
            Interlocked.Exchange(ref _quic, 0);
            Interlocked.Exchange(ref _passthrough, 0);
            Interlocked.Exchange(ref _sendErrors, 0);
            Interlocked.Exchange(ref _startedAtTicks, 0);
        }
    }
}
