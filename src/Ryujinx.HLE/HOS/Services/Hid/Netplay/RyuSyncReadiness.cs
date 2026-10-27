using System;

namespace Ryujinx.HLE.HOS.Services.Hid.Netplay
{
    // Captured immutably alongside a scheduled input, never taken from packet arrival order.
    internal readonly record struct RyuSyncReadinessStamp(long Epoch, uint Kind, bool Ready);

    internal sealed class RyuSyncReadiness
    {
        public RyuSyncReadinessStamp Local { get; private set; }
        private RyuSyncReadinessStamp _first;
        private RyuSyncReadinessStamp _second;
        private bool _released;

        public void Begin(uint kind)
        {
            if (kind == 0 || (Local.Epoch != 0 && !_released))
                throw new InvalidOperationException("SSBU changed loading stages before both peers were ready.");
            Local = new(checked(Local.Epoch + 1), kind, false);
            _released = false;
        }

        public void Commit(RyuSyncReadinessStamp first, RyuSyncReadinessStamp second)
        {
            if (first.Epoch != 0 && first.Epoch == second.Epoch && first.Kind != second.Kind)
                throw new InvalidOperationException("SSBU peers reached different loading stages. Stop and reconnect.");
            _first = first;
            _second = second;
        }

        public bool Poll(bool ready)
        {
            if (Local.Epoch == 0 || (Local.Ready && !ready))
                throw new InvalidOperationException("SSBU loading readiness changed unexpectedly. Stop and reconnect.");
            Local = Local with { Ready = ready };
            _released = ready && _first == Local && _second == Local;
            return _released;
        }
    }
}
