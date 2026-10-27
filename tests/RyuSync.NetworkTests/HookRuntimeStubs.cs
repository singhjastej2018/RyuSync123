// Only the emulator boundary is substituted. Hook sequencing/waiting is production code.
namespace Ryujinx.Cpu
{
    internal interface IExecutionContext
    {
        bool IsAarch32 { get; }
        bool Running { get; }
        ulong GetX(int index);
        void SetX(int index, ulong value);
        void StopRunning();
    }

    internal sealed class TestExecutionContext : IExecutionContext
    {
        public bool IsAarch32 { get; set; }
        public bool Running { get; private set; } = true;
        public ulong[] Registers { get; } = Enumerable.Range(0, 32).Select(i => (ulong)(0x10000 * i)).ToArray();
        public ulong GetX(int index) => Registers[index];
        public void SetX(int index, ulong value) => Registers[index] = value;
        public void StopRunning() => Running = false;
    }
}

namespace Ryujinx.HLE.HOS.Kernel.Threading
{
    internal sealed class KThread
    {
        public bool TerminationRequested { get; set; }
        public TestProcess Owner { get; } = new();
        public int Sleeps { get; private set; }
        public Action OnSleep { get; set; } = () => { };
        public void Sleep(long timeout)
        {
            Sleeps++;
            OnSleep();
            if (Sleeps > 10000) throw new Exception("Hook failed to cancel its wait");
        }
    }

    internal sealed class TestProcess
    {
        public TestMemory CpuMemory { get; } = new();
    }

    internal sealed class TestMemory
    {
        public Dictionary<ulong, object> Values { get; } = new();
        public T Read<T>(ulong address) => (T)Values[address];
    }
}

namespace Ryujinx.HLE
{
    internal sealed class Switch
    {
        public TestHid Hid { get; } = new();
        public TestSystem System { get; } = new();
    }

    internal sealed class TestHid
    {
        public TestNpads Npads { get; } = new();
    }

    internal sealed class TestNpads
    {
        public Func<long, bool> Commit { get; set; } = _ => true;
        public bool TryCommitSimulationStep(long step) => Commit(step);
    }
}

namespace Ryujinx.HLE.HOS.Kernel.Common
{
    internal abstract class KSynchronizationObject
    {
        public abstract bool IsSignaled();
    }
}

namespace Ryujinx.HLE.HOS.Kernel.Threading
{
    internal sealed class KReadableEvent : Ryujinx.HLE.HOS.Kernel.Common.KSynchronizationObject
    {
        private bool _signaled;
        public KReadableEvent(Ryujinx.HLE.TestKernelContext context) { }
        public void Signal() => _signaled = true;
        public void ClearIfSignaled() => _signaled = false;
        public override bool IsSignaled() => _signaled;
    }
}

namespace Ryujinx.HLE
{
    internal sealed class TestSystem
    {
        public TestKernelContext KernelContext { get; } = new();
    }

    internal sealed class TestKernelContext
    {
        public TestSynchronization Synchronization { get; } = new();
    }

    internal sealed class TestSynchronization
    {
        public Action OnWait { get; set; } = () => { };
        public int Waits { get; private set; }
        public int SignalsObserved { get; private set; }
        public void WaitFor(Ryujinx.HLE.HOS.Kernel.Common.KSynchronizationObject[] objects,
            long timeout, out int index)
        {
            if (timeout != 4_000_000) throw new Exception("UDP retry deadline changed");
            Waits++;
            index = Array.FindIndex(objects, item => item.IsSignaled());
            if (index >= 0) SignalsObserved++;
            else OnWait();
            if (Waits > 10000) throw new Exception("Hook failed to cancel input wait");
        }
    }
}
