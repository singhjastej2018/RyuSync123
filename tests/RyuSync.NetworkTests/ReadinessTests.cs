using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.HLE.HOS.Services.Hid.Netplay;
using System.Net;
using System.Reflection;

internal static class ReadinessTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly IPEndPoint Endpoint = new(IPAddress.Loopback, 24567);
    private static void Set(RyuSyncSession session, string name, object value) =>
        typeof(RyuSyncSession).GetField(name, Flags)!.SetValue(session, value);

    public static void Run()
    {
        for (int delay = 0; delay <= 8; delay++) CheckPair(delay);
        CheckPacketValidation();
        RyuSyncReadiness gate = new();
        Reject(() => gate.Poll(true));
        gate.Begin(1);
        Reject(() => gate.Begin(2));
        Require(!gate.Poll(true), "Unexchanged readiness released loading");
        Reject(() => gate.Poll(false));
        Reject(() => gate.Commit(new(1, 1, true), new(1, 2, true)));
        gate.Commit(new(1, 1, true), new(1, 1, true));
        Require(gate.Poll(true), "Matching committed readiness did not release loading");
        gate.Begin(1);
        Require(!gate.Poll(true), "Old ready packet released a repeated stage");
        Console.WriteLine("PASS: readiness releases both peers on the same buffered update for delays 0..8, asymmetric loading, retries, and repeated stages.");
    }

    private static void CheckPacketValidation()
    {
        RyuSyncSession session = (RyuSyncSession)Activator.CreateInstance(typeof(RyuSyncSession), nonPublic: true)!;
        session.SetInputDelayFrames(0);
        Set(session, "_role", RyuSyncRole.Host);
        Set(session, "_gameplayClockStarted", true);
        Set(session, "_remoteInputEndpoint", Endpoint);
        var receive = typeof(RyuSyncSession).GetMethod("HandleInputPacket", Flags)!;
        byte[] Packet(uint ready, bool secondInvalid = false)
        {
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);
            writer.Write((byte)RyuSyncSession.ProtocolVersion);
            writer.Write((byte)(secondInvalid ? 2 : 1));
            for (int i = 0; i < (secondInvalid ? 2 : 1); i++)
            {
                writer.Write((long)i);
                writer.Write((long)ControllerKeys.A);
                for (int axis = 0; axis < 4; axis++) writer.Write(0);
                writer.Write(1L);
                writer.Write(0x408B940U);
                writer.Write(i == 0 ? ready : 2U);
            }
            return stream.ToArray();
        }
        receive.Invoke(session, [Packet(1, true), Endpoint]);
        Require(!session.TryGetInputsForSimulationStep(0, default, out _, out _),
            "Invalid readiness packet partially committed a valid first frame");
        byte[] truncated = Packet(1)[..^1];
        receive.Invoke(session, [truncated, Endpoint]);
        Require(!session.TryGetInputsForSimulationStep(0, default, out _, out _), "Truncated packet supplied readiness");
        receive.Invoke(session, [Packet(0), Endpoint]);
        receive.Invoke(session, [Packet(1), Endpoint]);
        Require(session.TryGetInputsForSimulationStep(0, default, out _, out _), "Valid packet was rejected");
        session.GetReadiness(out _, out var remote);
        Require(!remote.Ready && remote.Epoch == 1, "Retransmission changed buffered readiness");
    }

    private static void CheckPair(int delay)
    {
        RyuSyncSession[] sessions = new RyuSyncSession[2];
        RyuSyncReadiness[] gates = [new(), new()];
        long[] steps = [0, 0];
        int[] stage = [0, 0];
        long[,] released = new long[2, 3];
        bool[] pending = [false, false];
        for (int i = 0; i < 2; i++)
        {
            sessions[i] = (RyuSyncSession)Activator.CreateInstance(typeof(RyuSyncSession), nonPublic: true)!;
            sessions[i].SetInputDelayFrames(delay);
            Set(sessions[i], "_role", i == 0 ? RyuSyncRole.Host : RyuSyncRole.Guest);
            Set(sessions[i], "_gameplayClockStarted", true);
            Set(sessions[i], "_remoteInputEndpoint", Endpoint);
        }
        var build = typeof(RyuSyncSession).GetMethod("BuildInputPacketLocked", Flags)!;
        var receive = typeof(RyuSyncSession).GetMethod("HandleInputPacket", Flags)!;
        // Alternate scheduling, deliberately lose and reorder redundant datagrams.
        byte[][] previous = new byte[2][];
        for (int cycle = 0; cycle < 2000 && (stage[0] < 3 || stage[1] < 3); cycle++)
        {
            for (int peer = 0; peer < 2; peer++)
            {
                if (stage[peer] >= 3) continue;
                var session = sessions[peer];
                if (!pending[peer]) session.SetReadiness(gates[peer].Local);
                pending[peer] = true;
                bool committed = session.TryGetInputsForSimulationStep(steps[peer], default, out _, out _);
                byte[] packet = (byte[])build.Invoke(session, null)!;
                if (cycle % 4 != 0)
                {
                    receive.Invoke(sessions[1 - peer], [packet, Endpoint]);
                    if (previous[peer] != null) receive.Invoke(sessions[1 - peer], [previous[peer], Endpoint]);
                }
                previous[peer] = packet;
                if (!committed) continue;
                pending[peer] = false;
                session.GetReadiness(out var local, out var remote);
                gates[peer].Commit(local, remote);
                long step = steps[peer]++;
                long start = stage[peer] == 0 ? (peer == 0 ? 2 : 6) : released[peer, stage[peer] - 1] + 1;
                if (step == start) gates[peer].Begin(0x408B940);
                if (step < start) continue;
                // The faster loader alternates between stages, with a nine-step gap.
                long readyAt = start + ((peer + stage[peer]) % 2 == 0 ? 2 : 11);
                if (gates[peer].Poll(step >= readyAt))
                {
                    released[peer, stage[peer]++] = step;
                }
            }
        }
        Require(stage[0] == 3 && stage[1] == 3, "Readiness deadlocked loading");
        for (int i = 0; i < 3; i++) Require(released[0, i] == released[1, i], "Peers released on different input updates");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Invalid readiness transition was accepted");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
