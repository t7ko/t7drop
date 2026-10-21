using System;
using System.Threading;

namespace T7Drop
{
    public enum Connection
    {
        NotPaired,
        Connecting,
        Online,
        Offline,
    }

    /// <summary>
    /// Runs the transfers on a thread of its own, as `t7drop receive --watch` does: sends what is
    /// queued, long-polls /sync while nothing else is to be done, receives, and after a failure
    /// tries again with exponential backoff and jitter, from 1 second to 5 minutes.
    /// Kick (a new item, a retry) ends a long poll or a pause; Restart (waking from sleep, a
    /// network change) also ends a transfer in flight, which may hang on a dead connection.
    /// </summary>
    class Worker
    {
        public const int LongPollSeconds = 50;
        const double MaxDelaySeconds = 300;

        readonly Store store;
        readonly Func<string> receiveFolder;
        readonly object gate = new object();
        readonly Thread thread;
        readonly Random random = new Random();
        bool stopping, kicked;
        double delay = 1;
        CancellationTokenSource round, poll;
        Transfers transfers;

        Connection state = Connection.Connecting;
        string problem, activity;
        DateTime nextTry;
        DateTime lostSince = DateTime.UtcNow;  // when the connection was last cut or failed; MinValue while it works

        /// <summary>Raised on the worker's thread whenever State, Problem, NextTry or Activity change.</summary>
        public event Action StatusChanged;
        /// <summary>Raised on the worker's thread for each item saved.</summary>
        public event Action<Received> Received;
        /// <summary>For tests, handed to Transfers.FailAck.</summary>
        public Func<string, bool> FailAck { get; set; }

        public Worker(Store store, Func<string> receiveFolder)
        {
            this.store = store;
            this.receiveFolder = receiveFolder;
            thread = new Thread(Loop) { IsBackground = true, Name = "t7drop worker" };
        }

        public Connection State { get { lock (gate) return state; } }
        public string Problem { get { lock (gate) return problem; } }
        public DateTime NextTry { get { lock (gate) return nextTry; } }  // UTC
        public string Activity { get { lock (gate) return activity; } }

        public void Start()
        {
            thread.Start();
        }

        public void Kick()
        {
            lock (gate)
            {
                kicked = true;
                if (poll != null)
                    poll.Cancel();
                Monitor.PulseAll(gate);
            }
        }

        public void Restart()
        {
            lock (gate)
            {
                kicked = true;
                delay = 1;
                if (lostSince == DateTime.MinValue)
                    lostSince = DateTime.UtcNow;
                if (round != null)
                    round.Cancel();
                Monitor.PulseAll(gate);
            }
            Log.Write("connecting again (woke up, or the network changed)");
        }

        public void Stop()
        {
            lock (gate)
            {
                stopping = true;
                if (round != null)
                    round.Cancel();
                Monitor.PulseAll(gate);
            }
            thread.Join(5000);
        }

        void Loop()
        {
            while (true)
            {
                Config config = store.LoadConfig();
                if (config == null)
                {
                    transfers = null;
                    SetStatus(Connection.NotPaired, null, DateTime.MinValue);
                    lock (gate)
                        kicked = false;  // pairing kicks again
                    if (!WaitForKick(Timeout.Infinite))
                        return;
                    continue;
                }
                if (transfers == null || transfers.Config.Token != config.Token)
                {
                    transfers = new Transfers(store, config, receiveFolder);
                    transfers.ItemReceived += OnReceived;
                    transfers.Activity += OnActivity;
                    transfers.FailAck = FailAck;
                    SetStatus(Connection.Connecting, null, DateTime.MinValue);
                }
                bool quick;
                CancellationTokenSource roundSource, pollSource;
                lock (gate)
                {
                    if (stopping)
                        return;
                    quick = kicked;
                    kicked = false;
                    round = roundSource = new CancellationTokenSource();
                    poll = pollSource = CancellationTokenSource.CreateLinkedTokenSource(roundSource.Token);
                }
                try
                {
                    transfers.RunOnce(quick ? 0 : LongPollSeconds, roundSource.Token, pollSource.Token);
                    DateTime lost;
                    lock (gate)
                    {
                        delay = 1;
                        lost = lostSince;
                        lostSince = DateTime.MinValue;
                    }
                    if (lost != DateTime.MinValue)
                        Log.Write("connected, " + (DateTime.UtcNow - lost).TotalSeconds.ToString("0.0") + " s after the connection was lost");
                    SetStatus(Connection.Online, null, DateTime.MinValue);
                }
                catch (OperationCanceledException)
                {
                    // kicked, restarted or stopped: the next round tells which
                }
                catch (Exception e)
                {
                    if (!Backoff(e))
                        return;
                }
                finally
                {
                    lock (gate)
                    {
                        round = null;
                        poll = null;
                    }
                    pollSource.Dispose();
                    roundSource.Dispose();
                }
            }
        }

        /// <summary>Waits before the next try; false if the worker is stopping.</summary>
        bool Backoff(Exception e)
        {
            var api = e as ApiException;
            bool transient = e is TransientException || (api != null && api.Transient);
            string reason = transient ? e.Message : e.GetType().Name + ": " + e.Message;
            if (!transient)
                Log.Write("error: " + e);
            double pause;
            lock (gate)
            {
                if (lostSince == DateTime.MinValue)
                    lostSince = DateTime.UtcNow;
                pause = delay * (0.5 + random.NextDouble() / 2);
                delay = Math.Min(delay * 2, MaxDelaySeconds);
            }
            Log.Write("not reachable (" + reason + "); trying again in " + pause.ToString("0.0") + " s");
            SetStatus(Connection.Offline, reason, DateTime.UtcNow.AddSeconds(pause));
            return WaitForKick((int)(pause * 1000));
        }

        /// <summary>Waits for a kick or the time; false if the worker is stopping.</summary>
        bool WaitForKick(int milliseconds)
        {
            lock (gate)
            {
                if (!kicked && !stopping)
                    Monitor.Wait(gate, milliseconds);
                return !stopping;
            }
        }

        void OnReceived(Received item)
        {
            Action<Received> handler = Received;
            if (handler != null)
                handler(item);
        }

        void OnActivity(string text)
        {
            lock (gate)
                activity = text;
            Changed();
        }

        void SetStatus(Connection newState, string newProblem, DateTime newNextTry)
        {
            lock (gate)
            {
                if (state == newState && problem == newProblem && nextTry == newNextTry)
                    return;
                state = newState;
                problem = newProblem;
                nextTry = newNextTry;
            }
            Changed();
        }

        void Changed()
        {
            Action handler = StatusChanged;
            if (handler != null)
                handler();
        }
    }
}
