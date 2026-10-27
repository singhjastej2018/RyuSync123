using Ryujinx.Common.Logging;
using Ryujinx.Cpu;
using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Memory;
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
        private readonly RyuSyncReadiness _readiness = new();
        private KThread _thread;
        private long _nextStart;
        private long _pacingTicks;
        private long _inputWaitTicks;
        private long _updateStarted;
        private readonly KReadableEvent _inputAvailable;
        private readonly Action _signalInput;
        private readonly KSynchronizationObject[] _inputWaitObjects;
        private bool _failed;
        private readonly RyuSyncDiagnostics _diagnostics;
        private RyuSyncBattleClock _before;
        private GamepadInput _p1;
        private GamepadInput _p2;
        private bool _inputsValid;
        private bool _diagnosticFailed;

        public RyuSyncSsbuHook(Switch device, ulong mainAddress, RyuSyncDiagnostics diagnostics = null)
        {
            _device = device;
            _mainAddress = mainAddress;
            _connection = RyuSyncSession.Instance.LaunchTransfer;
            _diagnostics = diagnostics;
            _inputAvailable = new KReadableEvent(device.System.KernelContext);
            _inputWaitObjects = [_inputAvailable];
            _signalInput = _inputAvailable.Signal;
        }

        public bool Handle(IExecutionContext context, KThread thread, ulong address, int id)
        {
            int offset = id switch
            {
                RyuSyncSsbuPatch.InputSvc => RyuSyncSsbuPatch.InputOffset,
                RyuSyncSsbuPatch.UpdateSvc => RyuSyncSsbuPatch.UpdateOffset,
                RyuSyncSsbuPatch.CompleteSvc => RyuSyncSsbuPatch.CompleteOffset,
                RyuSyncSsbuPatch.AsyncBeginSvc => RyuSyncSsbuPatch.AsyncBeginOffset,
                RyuSyncSsbuPatch.AsyncPollSvc => RyuSyncSsbuPatch.AsyncPollOffset,
                RyuSyncSsbuPatch.LoadBeginSvc => RyuSyncSsbuPatch.LoadBeginOffset,
                RyuSyncSsbuPatch.LoadPollSvc => RyuSyncSsbuPatch.LoadPollOffset,
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
                    session.SetReadiness(_readiness.Local);
                    _pacingTicks = 0;
                    _inputWaitTicks = 0;
                    long started = Stopwatch.GetTimestamp();
                    bool reportedWait = false;
                    session.InputAvailable += _signalInput;
                    try
                    {
                        while (!thread.TerminationRequested && context.Running)
                        {
                            // Clear BEFORE checking the predicate so arrival between the
                            // failed commit and WaitFor cannot lose its wakeup.
                            _inputAvailable.ClearIfSignaled();
                            if (!session.IsSessionEstablished || !ReferenceEquals(_connection, session.LaunchTransfer))
                            {
                                throw new InvalidOperationException("RyuSync disconnected while waiting for input. Stop the game to leave the session.");
                            }
                            long now = Stopwatch.GetTimestamp();
                            // Keep the existing cap until measurements establish redundancy.
                            // Sleep once for its remaining duration instead of polling.
                            if (now < _nextStart)
                            {
                                long remaining = _nextStart - now;
                                thread.Sleep(Math.Max(1, remaining * 1_000_000_000 / Stopwatch.Frequency));
                                _pacingTicks += Stopwatch.GetTimestamp() - now;
                                continue;
                            }
                            if (_device.Hid.Npads.TryCommitSimulationStep(_sequence.Step))
                            {
                                _nextStart = now + Stopwatch.Frequency / 60;
                                _sequence.InputsCommitted();
                                session.GetReadiness(out var localReadiness, out var remoteReadiness);
                                _readiness.Commit(localReadiness, remoteReadiness);
                                context.SetX(8, thread.Owner.CpuMemory.Read<byte>(context.GetX(25) + 0x4F4));
                                return true;
                            }
                            if (!reportedWait && now - started > 5 * Stopwatch.Frequency)
                            {
                                reportedWait = true;
                                Logger.Warning?.Print(LogClass.Hid, $"RyuSync is waiting at update {_sequence.Step}. Check both controllers and the connection. Stop the game to cancel.");
                            }
                            // Yield the emulated core, waking on input or disconnect.
                            // The bounded fallback preserves the 4 ms UDP retry cadence.
                            long waitStarted = Stopwatch.GetTimestamp();
                            _device.System.KernelContext.Synchronization.WaitFor(_inputWaitObjects, 4_000_000, out _);
                            _inputWaitTicks += Stopwatch.GetTimestamp() - waitStarted;
                        }
                        context.StopRunning();
                    }
                    finally
                    {
                        session.InputAvailable -= _signalInput;
                    }
                }
                else if (id == RyuSyncSsbuPatch.UpdateSvc)
                {
                    _sequence.BeforeUpdate();
                    context.SetX(0, thread.Owner.CpuMemory.Read<ulong>(context.GetX(31) + 0x288));
                    CaptureDiagnostics(thread, session, before: true);
                    _updateStarted = Stopwatch.GetTimestamp();
                }
                else if (id == RyuSyncSsbuPatch.AsyncBeginSvc || id == RyuSyncSsbuPatch.LoadBeginSvc)
                {
                    _sequence.RequireUpdating();
                    ulong state = context.GetX(0);
                    ulong table = thread.Owner.CpuMemory.Read<ulong>(state);
                    if (table < _mainAddress || table - _mainAddress > uint.MaxValue)
                        throw new InvalidOperationException("SSBU loading state has an unsupported dispatch table.");
                    uint kind = (uint)(table - _mainAddress);
                    bool supported = id == RyuSyncSsbuPatch.LoadBeginSvc ? kind == 0x408B940 :
                        kind is 0x408B850 or 0x408B8C8 or 0x408B9A0 or 0x408BA18;
                    if (!supported) throw new InvalidOperationException("SSBU loading state is not supported by this synchronization patch.");
                    _readiness.Begin(kind);
                    if (id == RyuSyncSsbuPatch.AsyncBeginSvc) context.SetX(19, state);
                    else context.SetX(0, context.GetX(31) + 8);
                }
                else if (id == RyuSyncSsbuPatch.AsyncPollSvc)
                {
                    _sequence.RequireUpdating();
                    uint pending = thread.Owner.CpuMemory.Read<uint>(context.GetX(0) + 0x30);
                    context.SetX(8, _readiness.Poll(pending == 0) ? 0UL : 1UL);
                }
                else if (id == RyuSyncSsbuPatch.LoadPollSvc)
                {
                    _sequence.RequireUpdating();
                    context.SetX(0, _readiness.Poll((context.GetX(0) & 1) != 0) ? 1UL : 0UL);
                    ulong stack = context.GetX(31);
                    context.SetX(29, thread.Owner.CpuMemory.Read<ulong>(stack + 0x60));
                    context.SetX(30, thread.Owner.CpuMemory.Read<ulong>(stack + 0x68));
                }
                else
                {
                    long updateTicks = Stopwatch.GetTimestamp() - _updateStarted;
                    CaptureDiagnostics(thread, session, before: false);
                    _diagnostics?.AddTiming(_pacingTicks, _inputWaitTicks, updateTicks);
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

        private void CaptureDiagnostics(KThread thread, RyuSyncSession session, bool before)
        {
            if (_diagnostics == null || _diagnosticFailed) return;
            try
            {
                RyuSyncBattleClock clock = RyuSyncBattleClock.Read(thread.Owner.CpuMemory.Read<uint>, _mainAddress);
                if (before)
                {
                    _before = clock;
                    _inputsValid = session.GetDiagnosticInputs(_sequence.Step, out _p1, out _p2);
                }
                else
                {
                    _diagnostics.Add(new(_sequence.Step, _inputsValid, _p1, _p2, _before, clock));
                }
            }
            catch (InvalidMemoryRegionException)
            {
                // Instrumentation must not disconnect or change the running game.
                _diagnosticFailed = true;
                _diagnostics.Fail("Battle clock could not be read; capture stopped.");
            }
        }
    }
}
