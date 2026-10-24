using Ryujinx.Common.Logging;
using System.Collections.Generic;
using System.Diagnostics;

namespace Ryujinx.HLE.HOS.Services.Hid.Netplay
{
    public sealed class RyuSyncNpadDevices : NpadDevices
    {
        private bool _pausedForBarrier;
        private bool _gameStartAnnounced;
        private readonly object _inputLock = new();
        private GamepadInput _localInput;
        private bool _hasLocalInput;
        private long _localInputTimestamp;

        public RyuSyncNpadDevices(Switch device, bool active = true) : base(device, active)
        {
        }

        public new void Configure(params System.ReadOnlySpan<ControllerConfig> configs)
        {
            RyuSyncSession session = RyuSyncSession.Instance;

            if (_device.Configuration.RyuSyncLaunch == null)
            {
                base.Configure(configs);
                return;
            }

            // RyuSync v1 is deliberately two-player SSBU only. Both peers expose the exact same
            // virtual controller topology to Horizon regardless of which physical controller is local.
            ControllerConfig[] netplayConfigs =
            [
                new ControllerConfig
                {
                    Player = PlayerIndex.Player1,
                    Type = ControllerType.ProController,
                },
                new ControllerConfig
                {
                    Player = PlayerIndex.Player2,
                    Type = ControllerType.ProController,
                },
            ];

            lock (_inputLock)
            {
                base.Configure(netplayConfigs);
            }

            if (!_gameStartAnnounced)
            {
                _gameStartAnnounced = true;
                session.NotifyGameStarted();

                if (!session.GameplayClockStarted && !_device.System.IsPaused)
                {
                    _device.System.TogglePauseEmulation(true);
                    _pausedForBarrier = true;
                    Logger.Info?.Print(LogClass.Hid, "RyuSync paused at the launch barrier.");
                }
            }
        }

        public new void Update(IList<GamepadInput> states)
        {
            RyuSyncSession session = RyuSyncSession.Instance;

            if (_device.Configuration.RyuSyncLaunch == null)
            {
                ReleaseNetplayPauseIfNeeded();
                base.Update(states);
                return;
            }

            GamepadInput localInput = default;
            bool foundLocalInput = false;

            // Each computer's configured local Player 1 controller is the physical source.
            // The session remaps it to shared P1 for the host or shared P2 for the guest.
            for (int i = 0; i < states.Count; i++)
            {
                if (states[i].PlayerId == PlayerIndex.Player1)
                {
                    localInput = states[i];
                    foundLocalInput = true;
                    break;
                }
            }

            lock (_inputLock)
            {
                _localInput = localInput;
                _hasLocalInput = foundLocalInput;
                _localInputTimestamp = Stopwatch.GetTimestamp();
            }

            if (session.IsSessionEstablished && !session.GameplayClockStarted)
            {
                PauseForBarrier();
                return;
            }

            // The host poller only samples the physical controller. The game hook
            // commits both virtual controllers immediately before SSBU polls them.
            // Release the launch pause so the game can reach that hook.
            if (session.GameplayClockStarted)
            {
                ReleaseNetplayPauseIfNeeded();
            }
        }

        internal bool TryCommitSimulationStep(long step)
        {
            lock (_inputLock)
            {
                if (!_hasLocalInput || Stopwatch.GetTimestamp() - _localInputTimestamp > Stopwatch.Frequency ||
                    !RyuSyncSession.Instance.TryGetInputsForSimulationStep(step, _localInput, out GamepadInput p1, out GamepadInput p2))
                {
                    return false;
                }
                GamepadInput[] inputs = [p1, p2];
                base.Update(inputs);
                return true;
            }
        }

        public new void UpdateSixAxis(IList<SixAxisInput> states)
        {
            if (_device.Configuration.RyuSyncLaunch == null)
            {
                base.UpdateSixAxis(states);
            }

            // Motion is intentionally unsupported in RyuSync v1. Do not feed independently sampled
            // motion data into the two emulators while netplay is active.
        }

        private void PauseForBarrier()
        {
            if (_pausedForBarrier || _device.System.IsPaused)
            {
                return;
            }

            _device.System.TogglePauseEmulation(true);
            _pausedForBarrier = true;
        }

        private void ReleaseNetplayPauseIfNeeded()
        {
            if (_pausedForBarrier && _device.System.IsPaused)
            {
                _device.System.TogglePauseEmulation(false);
            }

            _pausedForBarrier = false;
        }
    }
}
