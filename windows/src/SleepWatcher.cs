using System;
using System.Diagnostics.Eventing.Reader;
using Microsoft.Win32;

namespace T7Drop
{
    /// <summary>
    /// Tells when Windows goes to sleep and wakes up, with the times. Laptops with Modern Standby
    /// (S0 low power idle: most current ones, when the lid closes) do not send programs the sleep
    /// message; they pause them. Windows logs entering and leaving Modern Standby in the System
    /// log (Kernel-Power events 506 and 507), which any user may read. Classic sleep comes as
    /// PowerModeChanged. Both arrive on other threads, the log's possibly late, after a pause.
    /// </summary>
    sealed class SleepWatcher : IDisposable
    {
        public const string StandbyQuery =
            "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=506 or EventID=507)]]";
        const int EnteringStandby = 506;

        readonly Action<bool, DateTime> changed;  // true: going to sleep
        readonly EventLogWatcher watcher;

        /// <param name="changed">Called on another thread with true for sleep, false for waking.</param>
        public SleepWatcher(Action<bool, DateTime> changed)
        {
            this.changed = changed;
            try
            {
                watcher = new EventLogWatcher(new EventLogQuery("System", PathType.LogName, StandbyQuery));
                watcher.EventRecordWritten += OnStandbyEvent;
                watcher.Enabled = true;
            }
            catch (Exception)
            {
                watcher = null;  // no access to the log: classic sleep still comes through
            }
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }

        void OnStandbyEvent(object sender, EventRecordWrittenEventArgs e)
        {
            using (EventRecord record = e.EventRecord)
            {
                if (record != null)
                    changed(record.Id == EnteringStandby, record.TimeCreated ?? DateTime.Now);
            }
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
                changed(true, DateTime.Now);
            else if (e.Mode == PowerModes.Resume)
                changed(false, DateTime.Now);
        }

        public void Dispose()
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            if (watcher != null)
            {
                watcher.Enabled = false;
                watcher.Dispose();
            }
        }
    }
}
