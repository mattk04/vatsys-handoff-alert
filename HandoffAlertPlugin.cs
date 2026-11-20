using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Timers;
using vatsys;
using vatsys.Plugin;

namespace HandoffAlertPlugin
{
    [Export(typeof(IPlugin))]
    public class HandoffAlertPlugin : IPlugin
    {
        public string Name => "Handoff Alert";

        private readonly HashSet<string> _pendingHandoffs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pendingOutgoingHandoffs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FDP2.FDR> _subscribedFdrs =
            new Dictionary<string, FDP2.FDR>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        private readonly Timer _repeatTimer;
        private readonly Timer _handoffScanTimer;
        private readonly VscsButtonInjector _vscsInjector;
        private readonly HandoffAudioPlayer _audioPlayer;

        private bool _isAlertEnabled;

        public HandoffAlertPlugin()
        {
            _audioPlayer = new HandoffAudioPlayer();
            _vscsInjector = new VscsButtonInjector(OnToggleStateChanged);

            _repeatTimer = new Timer(5000); // Repeat every 5 seconds
            _repeatTimer.AutoReset = true;
            _repeatTimer.Elapsed += OnRepeatTimerElapsed;

            _handoffScanTimer = new Timer(250);
            _handoffScanTimer.AutoReset = true;
            _handoffScanTimer.Elapsed += OnHandoffScanTimerElapsed;

            FDP2.FDRsChanged += OnFDRsChanged;
            RefreshFdrSubscriptions();
            _handoffScanTimer.Start();
            _vscsInjector.Start();
        }

        private void OnToggleStateChanged(bool isEngaged)
        {
            _isAlertEnabled = isEngaged;

            if (!_isAlertEnabled)
            {
                _repeatTimer.Stop();
            }
            else
            {
                lock (_lock)
                {
                    if (_pendingHandoffs.Count > 0)
                    {
                        _audioPlayer.PlayHandoffAlert();
                        _repeatTimer.Start();
                    }
                }
            }
        }

        public void OnFDRUpdate(FDP2.FDR updated)
        {
            UpdatePendingHandoff(updated);
        }

        private void OnFDRsChanged(object sender, EventArgs e)
        {
            RefreshFdrSubscriptions();
        }

        private void OnHandoffScanTimerElapsed(object sender, ElapsedEventArgs e)
        {
            foreach (FDP2.FDR fdr in FDP2.GetFDRs.ToArray())
                UpdatePendingHandoff(fdr);
        }

        private void RefreshFdrSubscriptions()
        {
            FDP2.FDR[] fdrs = FDP2.GetFDRs.ToArray();
            int acceptedHandoffsRemoved = 0;

            lock (_lock)
            {
                HashSet<string> currentCallsigns = new HashSet<string>(
                    fdrs.Where(fdr => !string.IsNullOrEmpty(fdr.Callsign)).Select(fdr => fdr.Callsign),
                    StringComparer.OrdinalIgnoreCase);
                foreach (string callsign in _pendingOutgoingHandoffs.Where(callsign => !currentCallsigns.Contains(callsign)).ToArray())
                {
                    FDP2.FDR previousFdr;
                    if (_subscribedFdrs.TryGetValue(callsign, out previousFdr) && !previousFdr.IsTrackedByMe)
                        acceptedHandoffsRemoved++;

                    _pendingOutgoingHandoffs.Remove(callsign);
                }

                foreach (FDP2.FDR fdr in _subscribedFdrs.Values)
                    fdr.PropertyChanged -= OnFDRPropertyChanged;

                _subscribedFdrs.Clear();
                foreach (FDP2.FDR fdr in fdrs)
                {
                    if (string.IsNullOrEmpty(fdr.Callsign))
                        continue;

                    _subscribedFdrs[fdr.Callsign] = fdr;
                    fdr.PropertyChanged += OnFDRPropertyChanged;
                }
            }

            foreach (FDP2.FDR fdr in fdrs)
                UpdatePendingHandoff(fdr);

            if (_isAlertEnabled)
            {
                for (int i = 0; i < acceptedHandoffsRemoved; i++)
                    _audioPlayer.PlayHandoffAcceptedAlert();
            }
        }

        private void OnFDRPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != null &&
                e.PropertyName != nameof(FDP2.FDR.IsHandoff) &&
                e.PropertyName != nameof(FDP2.FDR.HandoffController) &&
                e.PropertyName != nameof(FDP2.FDR.HandoffSector) &&
                e.PropertyName != nameof(FDP2.FDR.IsTrackedByMe) &&
                e.PropertyName != nameof(FDP2.FDR.State))
            {
                return;
            }

            UpdatePendingHandoff(sender as FDP2.FDR);
        }

        private void UpdatePendingHandoff(FDP2.FDR updated)
        {
            if (updated == null || string.IsNullOrEmpty(updated.Callsign))
                return;

            bool isIncomingHandoff = IsIncomingHandoffForMe(updated);
            bool isOutgoingHandoff = IsOutgoingHandoffFromMe(updated);
            bool shouldTriggerImmediate = false;
            bool shouldTriggerAccepted = false;
            bool shouldTriggerRejected = false;

            lock (_lock)
            {
                if (isOutgoingHandoff)
                    _pendingOutgoingHandoffs.Add(updated.Callsign);

                if (_pendingOutgoingHandoffs.Contains(updated.Callsign) && !updated.IsTrackedByMe)
                {
                    _pendingOutgoingHandoffs.Remove(updated.Callsign);
                    shouldTriggerAccepted = true;
                }
                else if (!isOutgoingHandoff && _pendingOutgoingHandoffs.Remove(updated.Callsign))
                {
                    shouldTriggerRejected =
                        updated.IsTrackedByMe &&
                        updated.State == FDP2.FDR.FDRStates.STATE_UNCONTROLLED;
                }

                if (isIncomingHandoff)
                {
                    // Add returns true only when this is a newly received handoff
                    if (_pendingHandoffs.Add(updated.Callsign))
                    {
                        shouldTriggerImmediate = true;
                    }
                }
                else
                {
                    // Handoff was accepted, rejected, or withdrawn
                    _pendingHandoffs.Remove(updated.Callsign);
                    if (_pendingHandoffs.Count == 0)
                    {
                        _repeatTimer.Stop();
                    }
                }
            }

            if (shouldTriggerImmediate && _isAlertEnabled)
            {
                _audioPlayer.PlayHandoffAlert();
                if (!_repeatTimer.Enabled)
                {
                    _repeatTimer.Start();
                }
            }

            if (shouldTriggerAccepted && _isAlertEnabled)
                _audioPlayer.PlayHandoffAcceptedAlert();

            if (shouldTriggerRejected && _isAlertEnabled)
                _audioPlayer.PlayHandoffRejectedAlert();
        }

        public void OnRadarTrackUpdate(RDP.RadarTrack updated)
        {
            // Not required for handoff tracking
        }

        private bool IsIncomingHandoffForMe(FDP2.FDR fdr)
        {
            return MMI.FindTrack(fdr)?.State == MMI.HMIStates.HandoverIn;
        }

        private bool IsOutgoingHandoffFromMe(FDP2.FDR fdr)
        {
            return MMI.FindTrack(fdr)?.State == MMI.HMIStates.HandoverOut;
        }

        private void OnRepeatTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (!_isAlertEnabled)
            {
                _repeatTimer.Stop();
                return;
            }

            bool hasPending;
            lock (_lock)
            {
                // Prune any stale callsigns no longer in FDP2
                _pendingHandoffs.RemoveWhere(cs =>
                {
                    var fdr = FDP2.GetFDRs.Find(f => string.Equals(f.Callsign, cs, StringComparison.OrdinalIgnoreCase));
                    return fdr == null || !IsIncomingHandoffForMe(fdr);
                });

                hasPending = _pendingHandoffs.Count > 0;
            }

            if (hasPending)
            {
                _audioPlayer.PlayHandoffAlert();
            }
            else
            {
                _repeatTimer.Stop();
            }
        }
    }
}