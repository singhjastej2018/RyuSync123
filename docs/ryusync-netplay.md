# RyuSync SSBU sessions

Both players need this version of RyuSync (protocol 4) and **SSBU 1.0.0**. On each computer, configure
the physical controller as local Player 1. RyuSync maps the inviter to shared P1 and
the guest to shared P2.

In the RyuSync window, the host can choose **Frames to buffer** from 0 to 8 before
inviting (default 3). The guest adopts the host's value. The control is locked
while connected; disconnect to change it. The value lasts for this app session.
More buffering gives inputs longer to arrive, at the cost of extra delay.
The buffer counts numbered application updates. At 60 updates per second, each
adds about 16.7 ms; slower play increases that delay. This is experimental and
does not guarantee matching game state.

Connect, click Ready on both computers, then launch SSBU on both. Before either
emulated game starts, the host exports the selected user's committed SSBU account
save and the game's device save. The guest checks the archive's SHA-256 checksum
and prepares a temporary session copy. Both peers must acknowledge preparation
before launching. Transfers time out after three minutes; disconnect and reconnect
to retry. Saves larger than 256 MiB are rejected.

The SSBU version, firmware, language, region, time zone, docked mode and emulated
CPU clock multiplier must match. Physical CPU speeds do not need to match.
DLC and mods must also match; their contents are not yet verified
by the handshake.

The guest's game reads and writes the temporary copy, including after a network
disconnect. Normal game-save writes on the guest never switch back to their own
account/device save. Deleting entire save containers and raw save-storage access
are unsupported in guest sessions. Closing the game removes the temporary copy;
a process crash may leave it in the operating system's temporary directory. The
host uses their normal save and keeps progress. An absent host save produces an
empty session save, not a fallback to the guest's progress.

These are ordinary game saves, **not** CPU/RAM savestates. The session also shares
the initial emulated system time and the application's `csrng` byte stream. Host
cryptography is unchanged; emulated Internet access starts disabled for sessions.

## Cursor drift and determinism

The saved capture `20261023-201224` recorded three controller polls but four
application calls. Both battle counters advanced in each call, including the
extra call without another poll. Debugger pauses can trigger catch-up; this is
evidence of the mechanism, not its frequency in normal play.

For the exact supported main executable, the loader now applies a guarded patch
in memory. It forces the loop's input count to one and always takes the fresh-input
path. The catch-up queue still drains, but its extra application call is removed.
Before SSBU polls controllers, a private SVC waits for the numbered input pair and
commits both controllers once. Two more SVCs check the call/return order and emulate
the instructions they replace. Host time caps speed at 60 updates per second and
paces retries; it does not assign inputs to updates. The pacing wait sleeps for
its remaining duration. Missing input now waits on an emulated kernel event,
signaled by incoming controller packets, physical-controller samples or disconnect.
A 4 ms fallback keeps UDP retransmission working if a packet is lost. This yields
the emulated core rather than blocking a host thread while holding that core.
The event is cleared before testing input availability to avoid losing an arrival
between the failed input check and the wait. Missing input yields the
emulated core and logs a warning after five seconds. It does not impose a timeout
on legitimate loading or pauses; closing the game cancels the wait. On failure or
disconnect, the update thread stays stopped until the game is closed. Reconnecting
does not resume that running game. Other workers are not globally frozen by this hook.

The build ID and all affected instructions are checked before patching. Unsupported
executables or conflicting code patches fail launch rather than using the old
clock path. The game file is never modified. Translation disk caching is disabled
for patched sessions to avoid using or saving translations of different code.
Offline launches remain unchanged. Buffer selection stays in the RyuSync window.

This is a prototype, not verified deterministic lockstep. The application loop
covers menu dispatch as well as matches, but the new behavior in menus, transitions,
pause and slow motion still needs live verification. Forcing the ordinary path and
removing extra calls can change timing-dependent behavior. No comparison of game
memory verifies that the two simulations still agree.

Matching a seed does not synchronize CPU scheduling, timer reads, random-call
ordering, asynchronous services, or game-internal RNGs. Cursor and gameplay drift
can persist with this update. Re-aligning cursors at a screen edge only hides one
visible symptom; it does not repair the rest of the game state.

A complete fix needs simulation-frame lockstep with inputs consumed at equivalent
emulated points, controlled timing/randomness, and state checks to detect divergence
(plus resynchronization or rollback). Simply tying input to display refresh or
copying a save does not establish those guarantees, especially across Windows x64
and macOS arm64.

The hook calls `TryGetInputsForSimulationStep`. It consumes one pair per numbered
step, latches repeated requests and leaves a missing pair pending. It rejects
skipped steps and mixing step requests with the computer-clock path. Protocol 4
rejects older peers; both sides must use this behavior.

## Verification

### Capturing gameplay divergence

The diagnostic build keeps the last 8192 completed application updates in memory
(about 137 seconds at 60 updates/sec). It records the actual P1/P2 input pair and
six battle-clock words immediately before and after the existing application call.
The clock addresses are verified against the supported executable. No additional
breakpoints, game-memory writes, network messages or per-update disk writes are used.
Small read/record overhead remains; this is not a claim of zero timing impact.

Reproduce with Zelda versus Ganondorf on both PCs. Prefer the same named stage and
rules on both. As soon as a hit differs, both players should open RyuSync and click
**Export recent diagnostics**. Files are saved under Ryujinx's `Logs` directory;
the window displays the full path. Export remains available after stopping the
game, until another session starts or the app closes. Send both files. No game
code, saves or keys are included. A hash of the shared session seed identifies
matching sessions; the seed itself is not exported.

For analysis, run `python tools/compare_ryusync_diagnostics.py host.txt guest.txt`.
The comparison matches update numbers, not wall-clock timestamps. It rejects files
from different sessions/builds or two files from the same role. It reports the
earliest difference **within their retained overlap**, gaps and invalid input
metadata. Older divergence may be outside the retained window.

These observations do not include fighter positions, damage, actions or full RNG
state; those layouts remain unverified. Equal records do not prove equal gameplay.
Different battle clocks narrow the investigation but do not prove the cause.
The reads are on the application thread, not an atomic snapshot of all workers.
Do not force the clocks to agree or overwrite fighter state based only on this
capture. First compare both exports, then target the differing path for a fix.

`dotnet run --project tests/RyuSync.NetworkTests -c Release` exercises real TCP/UDP
input transport, host/guest controller mapping, chunked save transfer, startup
acknowledgements, isolation, cleanup, shared random bytes, incompatible settings,
corrupt archives, unsafe paths and disconnects. It requires no external packages.
It also checks numbered steps with buffer sizes 0â€“8 for both roles, including
delayed arrival, repeated reads, skipped steps and mixing clock modes.
It also tests atomic patch rejection, instruction encoding, fresh-input routing,
removal of the extra application call and valid/invalid update sequences. The
instruction guards were separately compared with the saved local SSBU excerpts.

After preparing the existing pinned LibHac package as in the build workflow,
`tests/RyuSync.SaveTests` verifies reads, writes, commit and read-only mounts using
the same filesystem implementation as the emulator. Both checks run in Windows
x64 and macOS arm64 CI. They do not replace two-computer SSBU gameplay testing.

### Wait timing and loading readiness

Exports additionally report cumulative `timed_updates`, `timing_frequency`,
`pacing_ticks`, `input_wait_ticks` and `application_ticks`. The comparison tool
prints the mean milliseconds per completed update for each category. These totals
cover the entire capture lifetime, not just the retained ring. Application time
is elapsed host time across the application call, including its own waits and
scheduling; it is not CPU-only simulation time. Controller conversion, other outer
loop work and diagnostics overhead are not included. Do not add these averages
and interpret the result as a complete frame time or proof of redundant pacing.
The 60 Hz cap remains enabled pending measurements.

The 2026-10-27 paired captures have 8,131 common updates (96 through 8226),
identical applied inputs throughout, and the first observed battle-clock difference
following update 2194. This establishes that equal outer updates alone do not
synchronize battle progression. It does not establish which loading branch caused
that particular divergence.

The supported executable contains named `StateInitializeAsync`, `StateLoadAsync`
and `StateSetupAsync` stages. The shared async-state poll at `main+0xE7E560`
checks a worker status before queuing a state pop. The load-state poll calls
`main+0xF84360`; its common return at `main+0xF84BB8` precedes the branch that
queues the load-state pop. Both are now gated on **committed** peer readiness.
The original worker status and battle counters are never written by the patch.

Each loading-stage entry gets a monotonically increasing epoch and a verified
main-relative dispatch-table identity. Each scheduled controller input carries a
16-byte immutable readiness stamp. Both peers consume the same pair of stamps
with the same numbered input pair. Local completion alone keeps the normal wait
state active; only matching, ready stamps for the current epoch release it.
Previous stages' buffered readiness cannot release a later or repeated stage.
Different stage identities for the same epoch, unexpected stage replacement,
and readiness regression stop the session.

This intentionally refines the proposed separate battle-input-clock design:
the controller poll and conversion stay at their original verified locations,
and the loading transition decisions are aligned with that existing input stream.
No controller repoll, second wait, or additional packet/round trip is introduced.
Workers and outer updates keep running while a stage waits, so the slower peer
can finish loading. Agreement incurs the existing buffer delay plus the next
sampling update during transitions. Steady gameplay input delay is unchanged.
The maximum extra UDP payload is 96 bytes per six-frame redundant packet.
Both peers must upgrade to protocol 4; protocol 3 packets are rejected.

Tests cover differing stage-entry and completion times, every buffer size 0–8,
dropped/reordered datagrams, repeated stage identities, register restoration,
and the absence of extra input commits or sleeps in loading hooks. All 29 code
guards were compared directly with the supported local executable. This is static
verification plus emulator-boundary tests, not a two-computer gameplay result.

Remaining limits: the input clock still numbers outer updates, and gates cover
the identified async initialization/loading/setup paths, not every possible
battle-internal transition, pause, timer, or worker decision. The original nine-step
divergence is not yet proven resolved. Existing post-update battle-clock captures
can detect residual differences; automatic fighter-state comparison and a separate
battle-step input clock are not implemented. No FPS improvement is claimed without
live measurement. The 60 Hz cap remains pending timing evidence.
