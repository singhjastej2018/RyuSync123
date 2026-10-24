using Ryujinx.Common.Logging;
using Ryujinx.Cpu;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System;
using System.Diagnostics;

namespace Ryujinx.HLE.HOS.Services.Hid.Netplay
{
    internal sealed class RyuSyncSsbuHook
    {
        private readonly Switch _device;
        private readonly ulong _mainAddress;
        private readonly RyuSyncLaunchTransfer _connection;
        private readonly RyuSyncStepSequence _sequence = new();
        private KThread _thread;
        private long _nextStart;
        private bool _failed;

        public RyuSyncSsbuHook(Switch device, ulong mainAddress)
        {
            _device = device;
            _mainAddress = mainAddress;
            _connection = RyuSyncSession.Instance.LaunchTransfer;
        }

        public bool Handle(IExecutionContext context, KThread thread, ulong address, int id)
        {
            int offset = id switch
            {
                RyuSyncSsbuPatch.InputSvc => RyuSyncSsbuPatch.InputOffset,
                RyuSyncSsbuPatch.UpdateSvc => RyuSyncSsbuPatch.UpdateOffset,
                RyuSyncSsbuPatch.CompleteSvc => RyuSyncSsbuPatch.CompleteOffset,
                _ => -1,
            };
            if (context.IsAarch32 || offset < 0 || address != _mainAddress + (ulong)offset)
            {
                return false;
            }

            RyuSyncSession session = RyuSyncSession.Instance;
            try
            {
                _thread ??= thread;
                if (_thread != thread || _failed || !session.IsSessionEstablished ||
                    !ReferenceEquals(_connection, session.LaunchTransfer))
                {
                    throw new InvalidOperationException("The RyuSync connection or SSBU update thread changed. Stop the game and reconnect.");
                }

                if (id == RyuSyncSsbuPatch.InputSvc)
                {
                    _sequence.Begin();
                    long started = Stopwatch.GetTimestamp();
                    bool reportedWait = false;
                    while (!thread.TerminationRequested && context.Running)
                    {
                        if (!session.IsSessionEstablished || !ReferenceEquals(_connection, session.LaunchTransfer))
                        {
                            throw new InvalidOperationException("RyuSync disconnected while waiting for input. Stop the game to leave the session.");
                        }
                        long now = Stopwatch.GetTimestamp();
                        // Host time only caps speed. It never selects or skips an input pair.
                        if (now >= _nextStart && _device.Hid.Npads.TryCommitSimulationStep(_sequence.Step))
                        {
                            _nextStart = now + Stopwatch.Frequency / 60;
                            _sequence.InputsCommitted();
                            // Preserve the replaced ldrb, even though the next branch is unconditional.
                            context.SetX(8, thread.Owner.CpuMemory.Read<byte>(context.GetX(25) + 0x4F4));
                            return true;
                        }
                        if (!reportedWait && now - started > 5 * Stopwatch.Frequency)
                        {
                            reportedWait = true;
                            Logger.Warning?.Print(LogClass.Hid, $"RyuSync is waiting at update {_sequence.Step}. Check both controllers and the connection. Stop the game to cancel.");
                        }
                        // Yield the emulated core, not just the host thread. Shutdown can wake this wait.
                        thread.Sleep(1_000_000);
                    }
                    context.StopRunning();
                }
                else if (id == RyuSyncSsbuPatch.UpdateSvc)
                {
                    _sequence.BeforeUpdate();
                    context.SetX(0, thread.Owner.CpuMemory.Read<ulong>(context.GetX(31) + 0x288));
                }
                else
                {
                    _sequence.Complete();
                    context.SetX(8, thread.Owner.CpuMemory.Read<uint>(context.GetX(26) + 0x4F8));
                    if (_sequence.Step == 1 || _sequence.Step % 600 == 0)
                    {
                        Logger.Info?.Print(LogClass.Hid, $"RyuSync completed numbered update {_sequence.Step - 1}. Experimental; game-state agreement is not verified.");
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                if (!_failed)
                {
                    _failed = true;
                    Logger.Error?.Print(LogClass.Hid, ex.Message);
                    session.FailSimulation(ex.Message);
                }
                // Never fall back to unsynchronized play or attach a running game to a new session.
                while (!thread.TerminationRequested && context.Running)
                {
                    thread.Sleep(10_000_000);
                }
                context.StopRunning();
            }
            return true;
        }
    }
}
