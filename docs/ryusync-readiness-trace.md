# SSBU 1.0.0 loading transition integration

Build: `FD471EB458C915E7CD13BBB67420632D00000000000000000000000000000000`.
All offsets are relative to main. Verified against the user's local executable;
no executable, key material, or disassembly dump is included in this repository.

## Verified control flow

- Outer controller poll: `0x278EFA4`; conversion: `0x278EFA8`.
- Outer application dispatch: `0x278F080`, calling `0x276D640`.
- Battle state-machine transition processing: `0xE7C9D0`, called at `0xE83068`.
- Selected state update: virtual slot `0x30`, called at `0xE830A0`.
- Shared battle update: `0xE7D800`; mode/slow-motion branches precede clock
  increments, then task dispatch at `0xE7DB90`. Multiple states share this update;
  it is not by itself a proven first-gameplay-frame boundary.
- State registration at `0xE6B868..0xE6B910` uses the executable's strings
  StateSampleAsync, StateInitializeAsync, StateLoadAsync, StateSetupAsync,
  StateFadeOutWhenEnding, StateReset, and StateExit.

## Gate sites

| SVC | Offset | Original operation | Handling |
| --- | --- | --- | --- |
| 7F03 | E7E490 | Copy x0 to x19 | Begin async-stage epoch, restore x19 |
| 7F04 | E7E560 | Read worker status at x0+30 into w8 | Observe completion; return zero only after committed agreement |
| 7F05 | E7E6E4 | Set x0 to sp+8 | Begin load-stage epoch, restore stack argument |
| 7F06 | F84BB8 | Restore x29/x30 from sp+60 | Gate returned readiness bit, restore both registers |

The shared async poll's zero path queues operation 4 (state pop); nonzero returns
without queuing it. The load poll's sole direct caller is `0xE7E74C`; a false
return branches to the epilogue, while true queues the same state-pop operation.
Load polling continues to execute its original resource checks while waiting.

Runtime checks accept shared async tables `0x408B850`, `0x408B8C8`, `0x408B9A0`,
`0x408BA18`, and load table `0x408B940`, and require the synchronized update thread
and update phase. Table identity is relative to main, never a process address.
The load state's constructor is registered under StateLoadAsync; initialize/setup
use the shared async-state enter and poll implementation.

## Why keep the existing controller poll

A gate inside the counter writer would run after controller conversion. Re-reading
HID there would not refresh already converted inputs and could duplicate edge
processing. Instead, the implementation changes when loading-state completion is
accepted, using the already buffered input pair as the common decision boundary.
This preserves original polling and leaves loading workers able to progress.

These checks establish specific control-flow points, not comprehensive game
state determinism. In particular, async worker activity before its completion
check may still influence other state. Two-computer validation is still required.
