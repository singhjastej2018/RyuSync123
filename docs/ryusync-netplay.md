# RyuSync SSBU sessions

Both players need this version of RyuSync (protocol 2). On each computer, configure
the physical controller as local Player 1. RyuSync maps the inviter to shared P1 and
the guest to shared P2.

In the RyuSync window, the host can choose **Frames to buffer** from 0 to 8 before
inviting (default 3). The guest adopts the host's value. The control is locked
while connected; disconnect to change it. The value lasts for this app session.
More buffering gives inputs longer to arrive, at the cost of extra delay.
These are still 60 Hz input steps, not verified SSBU updates; changing this value
does not establish lockstep or fix drift.

Connect, click Ready on both computers, then launch SSBU on both. Before either
emulated game starts, the host exports the selected user's committed SSBU account
save and the game's device save. The guest checks the archive's SHA-256 checksum
and prepares a temporary session copy. Both peers must acknowledge preparation
before launching. Transfers time out after three minutes; disconnect and reconnect
to retry. Saves larger than 256 MiB are rejected.

The SSBU version, firmware, language, region, time zone, docked mode and CPU clock
rate must match. DLC and mods must also match; their contents are not yet verified
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

Matching starting data is necessary but insufficient for deterministic play.
The current input loop advances at approximately 60 Hz using each host's
`Stopwatch`, independently of the game's actual simulation frames. A controller
state can therefore be observed for different numbers of game updates on the two
machines. HID initialization now populates its ring buffers directly instead of
injecting synthetic neutral states into the netplay input stream. Both controller
states are now committed before releasing a launch or input wait. This closes a
read-before-commit race, but does not tie input to a simulation step.
No comparison of game memory verifies
that the two simulations still agree.

Matching a seed does not synchronize CPU scheduling, timer reads, random-call
ordering, asynchronous services, or game-internal RNGs. Cursor and gameplay drift
can persist with this update. Re-aligning cursors at a screen edge only hides one
visible symptom; it does not repair the rest of the game state.

A complete fix needs simulation-frame lockstep with inputs consumed at equivalent
emulated points, controlled timing/randomness, and state checks to detect divergence
(plus resynchronization or rollback). Simply tying input to display refresh or
copying a save does not establish those guarantees, especially across Windows x64
and macOS arm64.

The session also has an internal `TryGetInputsForSimulationStep` path for a future
verified guest hook. It consumes one pair per numbered step, latches repeated
requests and leaves a missing pair pending. It rejects skipped steps and mixing
step requests with the computer-clock path. No guest hook calls it yet; normal
play still uses the existing clock. The internal path must not be enabled on one
peer without negotiating the same behavior on the other peer.

## Verification

`dotnet run --project tests/RyuSync.NetworkTests -c Release` exercises real TCP/UDP
input transport, host/guest controller mapping, chunked save transfer, startup
acknowledgements, isolation, cleanup, shared random bytes, incompatible settings,
corrupt archives, unsafe paths and disconnects. It requires no external packages.
It also checks numbered steps with buffer sizes 0–8 for both roles, including
delayed arrival, repeated reads, skipped steps and mixing clock modes.

After preparing the existing pinned LibHac package as in the build workflow,
`tests/RyuSync.SaveTests` verifies reads, writes, commit and read-only mounts using
the same filesystem implementation as the emulator. Both checks run in Windows
x64 and macOS arm64 CI. They do not replace two-computer SSBU gameplay testing.
