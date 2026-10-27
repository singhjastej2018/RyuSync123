"""Compare exported observations, not full game state. Uses only Python's standard library."""
import argparse
from pathlib import Path

CLOCK_FIELDS = ("raw_flags", "counter04", "slow_bytes", "counter0c", "rate_bits", "mode")


def load(path):
    lines = Path(path).read_text(encoding="utf-8-sig").splitlines()
    if not lines or lines[0] != "RYUSYNC_DIAGNOSTICS_V1":
        raise ValueError("Unsupported diagnostic format")
    metadata, samples = {}, {}
    last_step = -1
    for line in lines[1:]:
        if not line or line.startswith("#"):
            continue
        if "=" in line:
            key, value = line.split("=", 1)
            if key in metadata:
                raise ValueError("Duplicate metadata")
            metadata[key] = value
            continue
        parts = line.split("|")
        if len(parts) != 6 or parts[1] not in ("0", "1"):
            raise ValueError("Malformed diagnostic sample")
        step = int(parts[0])
        if step <= last_step:
            raise ValueError("Duplicate or unordered update")
        last_step = step
        inputs = []
        for part in parts[2:4]:
            values = part.split(",")
            if len(values) != 5:
                raise ValueError("Malformed controller input")
            inputs.append((int(values[0], 16), *(int(v) for v in values[1:])))
        clocks = [tuple(int(v, 16) for v in part.split(",")) for part in parts[4:]]
        if any(len(clock) != 6 for clock in clocks):
            raise ValueError("Malformed clock observation")
        samples[step] = (parts[1] == "1", tuple(inputs), *clocks)
    for key in ("session", "role", "build", "total", "error"):
        if key not in metadata:
            raise ValueError("Missing metadata: " + key)
    return metadata, samples


def compare(first, second):
    a_meta, a = first
    b_meta, b = second
    if any(not a_meta[key] or a_meta[key] != b_meta[key] for key in ("session", "build")):
        raise ValueError("Files are from different sessions or game builds")
    if {a_meta["role"], b_meta["role"]} != {"Host", "Guest"}:
        raise ValueError("Supply one host file and one guest file")
    common = sorted(a.keys() & b.keys())
    if not common:
        raise ValueError("No overlapping completed updates; export sooner on both computers")
    start, end = common[0], common[-1]
    gaps = end - start + 1 - len(common)
    invalid = sum(not a[step][0] or not b[step][0] for step in common)
    output = [f"Compared {len(common)} common updates: {start} through {end}.",
              f"Missing updates inside overlap: {gaps}; updates with unknown input metadata: {invalid}."]
    if start:
        output.append("Earlier updates are not covered. This cannot establish the first divergence of the whole match.")
    for meta in (a_meta, b_meta):
        if meta["error"]:
            output.append(f"{meta['role']} capture error: {meta['error']}")
    matched = sum(a[step][0] and b[step][0] and a[step][1] == b[step][1] for step in common)
    output.append(f"Identical applied inputs: {matched} of {len(common)} common updates.")
    for meta in (a_meta, b_meta):
        if "timed_updates" not in meta:
            continue
        count = int(meta["timed_updates"])
        frequency = int(meta["timing_frequency"])
        if count < 0 or frequency <= 0:
            raise ValueError("Invalid timing metadata")
        if count:
            averages = []
            for field, label in (("pacing_ticks", "pacing"), ("input_wait_ticks", "input wait"),
                                 ("application_ticks", "application")):
                ticks = int(meta[field])
                if ticks < 0:
                    raise ValueError("Invalid timing metadata")
                averages.append(f"{label} {ticks * 1000 / frequency / count:.3f} ms")
            output.append(f"{meta['role']} averages over {count} completed timed updates: " + ", ".join(averages) + ".")
    found = False
    for step in common:
        left, right = a[step], b[step]
        differences = []
        if left[0] and right[0] and left[1] != right[1]:
            differences.append("applied controller inputs differ")
        for index, phase in ((2, "before"), (3, "after")):
            for field, x, y in zip(CLOCK_FIELDS, left[index], right[index]):
                if x != y:
                    differences.append(f"{phase}.{field}: {x:08X} versus {y:08X}")
        if differences:
            output.append(f"Earliest observed difference in retained overlap: update {step}.")
            output.extend(differences)
            found = True
            break
    if not found:
        output.append("No differences in the comparable recorded fields. This does NOT establish matching fighter state.")
    output.append("Battle counters can pause/reset; their differences are diagnostic evidence, not a proven cause.")
    return "\n".join(output)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("first")
    parser.add_argument("second")
    args = parser.parse_args()
    try:
        print(compare(load(args.first), load(args.second)))
    except (ValueError, KeyError, OSError) as error:
        parser.exit(2, str(error) + "\n")
