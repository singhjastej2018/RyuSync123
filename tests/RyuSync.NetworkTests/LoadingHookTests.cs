using Ryujinx.Cpu;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Hid.Netplay;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

internal static class LoadingHookTests
{
    public static async Task Run()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using TcpClient client = new();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using TcpClient peer = await listener.AcceptTcpClientAsync();
        RyuSyncSession session = RyuSyncSession.Instance;
        void Set(string field, object value) => typeof(RyuSyncSession).GetField(field, flags)!.SetValue(session, value);
        Set("_controlClient", client);
        Set("_accepted", true);
        try
        {
            const ulong main = 0x8504000, state = 0x12340000, stack = 0x23450000;
            Switch device = new();
            KThread thread = new();
            thread.OnSleep = () => thread.TerminationRequested = true;
            TestExecutionContext context = new();
            RyuSyncSsbuHook hook = new(device, main);
            int commits = 0;
            device.Hid.Npads.Commit = _ => { commits++; return true; };
            void Call(int offset, int svc)
            {
                if (!hook.Handle(context, thread, main + (ulong)offset, svc) || !context.Running)
                    throw new Exception("Loading hook rejected a valid call");
            }
            void Begin(long epoch, uint kind)
            {
                Set("_committedLocalReadiness", new RyuSyncReadinessStamp(epoch, kind, epoch != 0));
                Set("_committedRemoteReadiness", new RyuSyncReadinessStamp(epoch, kind, epoch != 0));
                typeof(RyuSyncSsbuHook).GetField("_nextStart", flags)!.SetValue(hook, 0L);
                context.SetX(31, stack);
                thread.Owner.CpuMemory.Values[context.GetX(25) + 0x4F4] = (byte)1;
                thread.Owner.CpuMemory.Values[stack + 0x288] = 0x12345678UL;
                thread.Owner.CpuMemory.Values[context.GetX(26) + 0x4F8] = 0U;
                Call(RyuSyncSsbuPatch.InputOffset, RyuSyncSsbuPatch.InputSvc);
                Call(RyuSyncSsbuPatch.UpdateOffset, RyuSyncSsbuPatch.UpdateSvc);
            }
            void End() => Call(RyuSyncSsbuPatch.CompleteOffset, RyuSyncSsbuPatch.CompleteSvc);
            Begin(0, 0);
            thread.Owner.CpuMemory.Values[state] = main + 0x408B9A0;
            context.SetX(0, state);
            Call(RyuSyncSsbuPatch.AsyncBeginOffset, RyuSyncSsbuPatch.AsyncBeginSvc);
            Require(context.GetX(19) == state, "Async enter did not restore x19");
            thread.Owner.CpuMemory.Values[state + 0x30] = 0U;
            Call(RyuSyncSsbuPatch.AsyncPollOffset, RyuSyncSsbuPatch.AsyncPollSvc);
            Require(context.GetX(8) == 1, "A locally complete async stage released before agreement");
            End();
            Begin(1, 0x408B9A0);
            context.SetX(0, state);
            Call(RyuSyncSsbuPatch.AsyncPollOffset, RyuSyncSsbuPatch.AsyncPollSvc);
            Require(context.GetX(8) == 0, "An agreed async stage stayed pending");
            Require((uint)thread.Owner.CpuMemory.Values[state + 0x30] == 0, "Gate modified game readiness memory");
            End();
            Begin(1, 0x408B9A0);
            thread.Owner.CpuMemory.Values[state] = main + 0x408B940;
            context.SetX(0, state);
            Call(RyuSyncSsbuPatch.LoadBeginOffset, RyuSyncSsbuPatch.LoadBeginSvc);
            Require(context.GetX(0) == stack + 8, "Load enter did not restore its stack argument");
            void PollLoad(ulong expected)
            {
                context.SetX(0, 1);
                thread.Owner.CpuMemory.Values[stack + 0x60] = 0xABCDUL;
                thread.Owner.CpuMemory.Values[stack + 0x68] = main + 0xE7E750;
                Call(RyuSyncSsbuPatch.LoadPollOffset, RyuSyncSsbuPatch.LoadPollSvc);
                Require(context.GetX(0) == expected, "Load gate returned the wrong readiness");
                Require(context.GetX(29) == 0xABCD && context.GetX(30) == main + 0xE7E750,
                    "Load return did not restore frame pointer and return address");
            }
            PollLoad(0);
            End();
            Begin(2, 0x408B940);
            PollLoad(1);
            End();
            Require(commits == 4 && thread.Sleeps == 0 && device.System.KernelContext.Synchronization.Waits == 0,
                "Loading gate added an input consumption or a blocking wait");
            Console.WriteLine("PASS: loading hooks preserve guest register semantics, delay only transition decisions, and add no waits or input reads.");
        }
        finally { session.Disconnect(); }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
