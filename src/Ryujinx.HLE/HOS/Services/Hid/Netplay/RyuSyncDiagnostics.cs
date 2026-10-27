using System;
using System.Globalization;
using System.Diagnostics;
using System.Text;

namespace Ryujinx.HLE.HOS.Services.Hid.Netplay
{
    internal readonly record struct RyuSyncBattleClock(uint Flags, uint Counter04, uint SlowBytes,
        uint Counter0C, uint RateBits, uint Mode)
    {
        internal static RyuSyncBattleClock Read(Func<ulong, uint> read, ulong main)
        {
            ulong clock = main + 0x64548A0;
            return new(read(clock), read(clock + 4), read(clock + 8), read(clock + 12),
                read(clock + 16), read(main + 0x4319CC0));
        }

        public override string ToString() =>
            $"{Flags:X8},{Counter04:X8},{SlowBytes:X8},{Counter0C:X8},{RateBits:X8},{Mode:X8}";
    }

    internal readonly record struct RyuSyncDiagnosticSample(long Step, bool InputsValid,
        GamepadInput P1, GamepadInput P2, RyuSyncBattleClock Before, RyuSyncBattleClock After);

    // No network traffic, file I/O or log formatting on the emulated update thread.
    // These scalars are observations, not a checksum of the complete game state.
    public sealed class RyuSyncDiagnostics
    {
        public const int Capacity = 8192;
        private readonly object _sync = new();
        private readonly RyuSyncDiagnosticSample[] _samples = new RyuSyncDiagnosticSample[Capacity];
        private readonly string _sessionId;
        private readonly string _role;
        private int _next;
        private int _count;
        private long _total;
        private long _timedUpdates;
        private long _pacingTicks;
        private long _inputWaitTicks;
        private long _updateTicks;
        private string _error = string.Empty;

        internal RyuSyncDiagnostics(string sessionId, RyuSyncRole role)
        {
            _sessionId = sessionId;
            _role = role.ToString();
        }

        internal void Add(RyuSyncDiagnosticSample sample)
        {
            lock (_sync)
            {
                _samples[_next] = sample;
                _next = (_next + 1) % Capacity;
                _count = Math.Min(_count + 1, Capacity);
                _total++;
            }
        }

        internal void AddTiming(long pacingTicks, long inputWaitTicks, long updateTicks)
        {
            lock (_sync)
            {
                _timedUpdates++;
                _pacingTicks += pacingTicks;
                _inputWaitTicks += inputWaitTicks;
                _updateTicks += updateTicks;
            }
        }

        internal void Fail(string reason)
        {
            lock (_sync) _error = reason;
        }

        public string Export()
        {
            RyuSyncDiagnosticSample[] samples;
            long total;
            string error;
            long timedUpdates, pacingTicks, inputWaitTicks, updateTicks;
            lock (_sync)
            {
                samples = new RyuSyncDiagnosticSample[_count];
                int start = (_next - _count + Capacity) % Capacity;
                for (int i = 0; i < _count; i++) samples[i] = _samples[(start + i) % Capacity];
                total = _total;
                error = _error;
                timedUpdates = _timedUpdates;
                pacingTicks = _pacingTicks;
                inputWaitTicks = _inputWaitTicks;
                updateTicks = _updateTicks;
            }

            StringBuilder output = new();
            output.AppendLine("RYUSYNC_DIAGNOSTICS_V1");
            output.AppendLine($"session={_sessionId}");
            output.AppendLine($"role={_role}");
            output.AppendLine($"build={RyuSyncSsbuPatch.BuildId}");
            output.AppendLine(FormattableString.Invariant($"total={total}"));
            output.AppendLine($"error={error}");
            // Optional V1 metadata: all completed timed updates, not only the ring.
            // These categories do not cover the entire outer loop.
            output.AppendLine(FormattableString.Invariant($"timed_updates={timedUpdates}"));
            output.AppendLine(FormattableString.Invariant($"timing_frequency={Stopwatch.Frequency}"));
            output.AppendLine(FormattableString.Invariant($"pacing_ticks={pacingTicks}"));
            output.AppendLine(FormattableString.Invariant($"input_wait_ticks={inputWaitTicks}"));
            output.AppendLine(FormattableString.Invariant($"application_ticks={updateTicks}"));
            output.AppendLine("# step|inputs_valid|p1(buttons,lx,ly,rx,ry)|p2|before(flags,c04,slow,c0c,rate,mode)|after");
            foreach (var sample in samples)
            {
                output.AppendLine(string.Join('|', sample.Step.ToString(CultureInfo.InvariantCulture),
                    sample.InputsValid ? "1" : "0", Input(sample.P1), Input(sample.P2),
                    sample.Before.ToString(), sample.After.ToString()));
            }
            return output.ToString();
        }

        private static string Input(GamepadInput input) => FormattableString.Invariant(
            $"{(ulong)input.Buttons:X16},{input.LStick.Dx},{input.LStick.Dy},{input.RStick.Dx},{input.RStick.Dy}");
    }
}
