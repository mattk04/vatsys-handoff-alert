using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
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
        private readonly object _lock = new object();
        private readonly Timer _repeatTimer;
        private readonly VscsButtonInjector _vscsInjector;
        private readonly HandoffAudioPlayer _audioPlayer;

        private bool _isAlertEnabled = true;

        public HandoffAlertPlugin()
        {
            _audioPlayer = new HandoffAudioPlayer();
            _vscsInjector = new VscsButtonInjector(OnToggleStateChanged);

            _repeatTimer = new Timer(5000); // Repeat every 5 seconds
            _repeatTimer.AutoReset = true;
            _repeatTimer.Elapsed += OnRepeatTimerElapsed;

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
            if (updated == null || string.IsNullOrEmpty(updated.Callsign))
                return;

            bool isIncomingHandoff = IsIncomingHandoffForMe(updated);
            bool shouldTriggerImmediate = false;

            lock (_lock)
            {
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
        }

        public void OnRadarTrackUpdate(RDP.RadarTrack updated)
        {
            // Not required for handoff tracking
        }

        private bool IsIncomingHandoffForMe(FDP2.FDR fdr)
        {
            // Ensure we are connected and have an active controller position
            if (!Network.IsConnected || Network.Me == null)
                return false;

            // IsHandoff distinguishes an actual offered handoff from ordinary tracking.
            if (!fdr.IsHandoff)
                return false;

            // Check whether the handoff is offered directly to our position.
            if (fdr.HandoffController == null)
                return false;

            bool targetedAtMe =
                string.Equals(fdr.HandoffController.Callsign, Network.Me.Callsign, StringComparison.OrdinalIgnoreCase) ||
                (Network.Me.Position != null && fdr.HandoffController.Position == Network.Me.Position);

            // Once accepted or withdrawn, vatSys clears HandoffController.
            return targetedAtMe;
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