"""Analyze Phase 1 vs Phase 2 debug data with phase tag (bit 1 of occluded field)."""
import numpy as np
from pathlib import Path
from collections import defaultdict

dump_dir = Path("dump")
debug_path = dump_dir / "debug_hiz.bin"
raw = np.fromfile(str(debug_path), dtype=np.uint8)
count = int(np.frombuffer(raw[:4], dtype=np.uint32)[0])
count = min(count, 4096)

dt = np.dtype([
    ('minUv_x', 'f4'), ('minUv_y', 'f4'),
    ('maxUv_x', 'f4'), ('maxUv_y', 'f4'),
    ('nearDepth', 'f4'), ('hizDepth', 'f4'),
    ('mipLevel', 'u4'), ('flags', 'u4'),  # bit0=occluded, bit1=usePrev(Phase1)
    ('cx', 'f4'), ('cy', 'f4'), ('cz', 'f4'), ('radius', 'f4'),
])
recs = np.frombuffer(raw[4:4 + count * 48], dtype=dt, count=count)
W, H = 2560, 1351

occluded = (recs['flags'] & 1).astype(bool)
is_phase1 = (recs['flags'] & 2).astype(bool)

print(f"=== {count} records total ===")
print(f"  Phase 1 records: {np.sum(is_phase1)} (usePrev=true)")
print(f"  Phase 2 records: {np.sum(~is_phase1)} (usePrev=false)")
print(f"  Phase 1 occluded: {np.sum(occluded & is_phase1)}")
print(f"  Phase 1 visible: {np.sum(~occluded & is_phase1)}")
print(f"  Phase 2 occluded: {np.sum(occluded & ~is_phase1)}")
print(f"  Phase 2 visible: {np.sum(~occluded & ~is_phase1)}")

# Group by world position to find clusters in both phases
groups = defaultdict(list)
for i, item in enumerate(recs):
    key = (round(float(item['cx']),3), round(float(item['cy']),3), 
           round(float(item['cz']),3), round(float(item['radius']),3))
    groups[key].append(i)

# Find clusters that are OCC in Phase 1 then OCC in Phase 2 (doubly culled = truly lost)
doubly_culled = []
for key, indices in groups.items():
    p1_results = [(i, recs[i]) for i in indices if is_phase1[i]]
    p2_results = [(i, recs[i]) for i in indices if not is_phase1[i]]
    if p1_results and p2_results:
        p1_occ = all(occluded[i] for i, _ in p1_results)
        p2_occ = all(occluded[i] for i, _ in p2_results)
        if p1_occ and p2_occ:
            doubly_culled.append((key, p1_results, p2_results))

print(f"\n=== Doubly culled clusters (Phase1 OCC + Phase2 OCC): {len(doubly_culled)} ===")
for key, p1_results, p2_results in doubly_culled[:10]:
    cx, cy, cz, r = key
    cx_pix = (p1_results[0][1]['minUv_x'] + p1_results[0][1]['maxUv_x']) / 2 * W
    cy_pix = (p1_results[0][1]['minUv_y'] + p1_results[0][1]['maxUv_y']) / 2 * H
    
    p1 = p1_results[0][1]
    p2 = p2_results[0][1]
    print(f"\n  World=({cx},{cy},{cz}) r={r} screen=({cx_pix:.0f},{cy_pix:.0f})")
    print(f"    Phase1: mip={p1['mipLevel']} near={p1['nearDepth']:.6f} hiz={p1['hizDepth']:.6f} diff={p1['nearDepth']-p1['hizDepth']:+.8f}")
    print(f"    Phase2: mip={p2['mipLevel']} near={p2['nearDepth']:.6f} hiz={p2['hizDepth']:.6f} diff={p2['nearDepth']-p2['hizDepth']:+.8f}")
    
    hiz_match = abs(float(p1['hizDepth']) - float(p2['hizDepth'])) < 1e-7
    print(f"    hizDepth match: {hiz_match}")

# Check if hizDepth differs between phases for ANY cluster
print(f"\n=== hizDepth comparison across phases ===")
hiz_diffs = []
for key, indices in groups.items():
    p1_items = [recs[i] for i in indices if is_phase1[i]]
    p2_items = [recs[i] for i in indices if not is_phase1[i]]
    if p1_items and p2_items:
        d = abs(float(p1_items[0]['hizDepth']) - float(p2_items[0]['hizDepth']))
        hiz_diffs.append(d)

hiz_diffs = np.array(hiz_diffs)
print(f"  Clusters appearing in both phases: {len(hiz_diffs)}")
print(f"  hizDepth identical: {np.sum(hiz_diffs < 1e-7)}")
print(f"  hizDepth different: {np.sum(hiz_diffs >= 1e-7)}")
if np.any(hiz_diffs >= 1e-7):
    print(f"  Max hizDepth difference: {np.max(hiz_diffs):.8f}")
else:
    print(f"  ALL hizDepth values are IDENTICAL between Phase 1 and Phase 2!")
    print(f"  => Phase 2 is reading from the SAME HiZ texture as Phase 1")
