"""
HiZ Debug Analyzer — reads frame dumps from the engine and visualizes
the HiZ / depth data for occlusion culling debugging.

Usage:
    uv run --with numpy --with matplotlib python tools/analyze_hiz.py [dump_dir]

Default dump_dir: dump/
"""

import json
import struct
import sys
from pathlib import Path

import numpy as np
import matplotlib.pyplot as plt
import matplotlib.patches as patches
from matplotlib.lines import Line2D


def load_bin(path: Path) -> np.ndarray:
    """Load a .bin file with (width, height) header followed by R32_Float data."""
    with open(path, "rb") as f:
        width = struct.unpack("<I", f.read(4))[0]
        height = struct.unpack("<I", f.read(4))[0]
        data = np.frombuffer(f.read(), dtype=np.float32)
        return data.reshape((height, width))


def load_debug_hiz(path: Path):
    """Load debug_hiz.bin: uint32 count, then count * 48-byte records."""
    raw = np.fromfile(str(path), dtype=np.uint8)
    if len(raw) < 4:
        return []
    count = int(np.frombuffer(raw[:4], dtype=np.uint32)[0])
    count = min(count, 4096)
    if count == 0:
        return []

    dt = np.dtype([
        ('minUv_x', 'f4'), ('minUv_y', 'f4'),
        ('maxUv_x', 'f4'), ('maxUv_y', 'f4'),
        ('nearDepth', 'f4'), ('hizDepth', 'f4'),
        ('mipLevel', 'u4'), ('occluded', 'u4'),
        ('cx', 'f4'), ('cy', 'f4'), ('cz', 'f4'), ('radius', 'f4'),
    ])
    records = np.frombuffer(raw[4:4 + count * 48], dtype=dt, count=count)
    return records


def main():
    dump_dir = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("dump")

    if not dump_dir.exists():
        print(f"Error: dump directory '{dump_dir}' not found.")
        sys.exit(1)

    # Load metadata
    meta = None
    meta_path = dump_dir / "meta.json"
    if meta_path.exists():
        with open(meta_path) as f:
            meta = json.load(f)
        print("=== Frame Metadata ===")
        print(f"  Resolution:     {meta['Width']}x{meta['Height']}")
        print(f"  HiZ Mip Count:  {meta['MipCount']}")
        print(f"  CameraPos:      {meta['CameraPos']}")
        print()

    # --- Load all mips ---
    mip_data = []
    for mip in range(20):
        mip_path = dump_dir / f"hiz_mip{mip}.bin"
        if not mip_path.exists():
            break
        mip_data.append(load_bin(mip_path))

    depth = None
    depth_path = dump_dir / "depth_mip0.bin"
    if depth_path.exists():
        depth = load_bin(depth_path)

    # --- Load debug AABBs ---
    debug_records = []
    debug_path = dump_dir / "debug_hiz.bin"
    if debug_path.exists():
        debug_records = load_debug_hiz(debug_path)

    if len(debug_records) > 0:
        n_occluded = int(np.sum(debug_records['occluded']))
        n_visible = len(debug_records) - n_occluded
        print(f"=== Debug HiZ: {len(debug_records)} clusters ===")
        print(f"  Occluded: {n_occluded}  Visible: {n_visible}")

        # Filter out full-screen entries
        fullscreen = ((debug_records['minUv_x'] == 0) & (debug_records['minUv_y'] == 0) &
                      (debug_records['maxUv_x'] == 1) & (debug_records['maxUv_y'] == 1))
        n_fs = int(np.sum(fullscreen))
        print(f"  Full-screen (near plane): {n_fs}")
        print(f"  Valid projection: {len(debug_records) - n_fs}")

        # Per-mip breakdown
        for mip in range(len(mip_data)):
            mask = debug_records['mipLevel'] == mip
            if np.any(mask):
                n = int(np.sum(mask))
                n_occ = int(np.sum(debug_records['occluded'][mask]))
                print(f"  Mip {mip:2d}: {n:4d} clusters ({n_occ} occluded, {n - n_occ} visible)")
        print()
    else:
        print("=== Debug HiZ: 0 clusters (enable DebugShowHiZAABBs in UI) ===\n")

    # ==========================================
    # Figure 1: 2D Projections on Depth Buffer
    # ==========================================
    if depth is not None and len(debug_records) > 0:
        h, w = depth.shape

        fig, axes = plt.subplots(1, 3, figsize=(22, 7))

        # --- Panel 1: All projections on depth ---
        ax = axes[0]
        ax.imshow(depth, cmap="inferno", vmin=0, vmax=1, aspect='equal')
        ax.set_title(f"All Cluster Projections ({len(debug_records)})", fontsize=11)

        for item in debug_records:
            x0 = float(item['minUv_x']) * w
            y0 = float(item['minUv_y']) * h
            x1 = float(item['maxUv_x']) * w
            y1 = float(item['maxUv_y']) * h
            rw = max(1, x1 - x0)
            rh = max(1, y1 - y0)

            is_occ = bool(item['occluded'])
            color = '#00ff00' if is_occ else '#ff4444'
            alpha = 0.15 if is_occ else 0.5
            lw = 0.3 if is_occ else 0.8

            rect = patches.Rectangle(
                (x0, y0), rw, rh,
                linewidth=lw, edgecolor=color, facecolor='none', alpha=alpha
            )
            ax.add_patch(rect)

        legend_elements = [
            Line2D([0], [0], color='#ff4444', lw=2, label='Visible'),
            Line2D([0], [0], color='#00ff00', lw=2, label='Occluded'),
        ]
        ax.legend(handles=legend_elements, loc='upper right', fontsize=9)

        # --- Panel 2: Only visible (not occluded) ---
        ax2 = axes[1]
        ax2.imshow(depth, cmap="inferno", vmin=0, vmax=1, aspect='equal')
        vis_mask = ~debug_records['occluded'].astype(bool)
        vis = debug_records[vis_mask]
        ax2.set_title(f"Visible Only ({len(vis)})", fontsize=11)

        for item in vis:
            x0 = float(item['minUv_x']) * w
            y0 = float(item['minUv_y']) * h
            x1 = float(item['maxUv_x']) * w
            y1 = float(item['maxUv_y']) * h
            rw = max(1, x1 - x0)
            rh = max(1, y1 - y0)

            # Color by mip level
            mip = int(item['mipLevel'])
            cmap_mip = plt.cm.rainbow(mip / max(1, len(mip_data) - 1))
            rect = patches.Rectangle(
                (x0, y0), rw, rh,
                linewidth=0.8, edgecolor=cmap_mip, facecolor='none', alpha=0.7
            )
            ax2.add_patch(rect)

        # --- Panel 3: nearDepth vs hizDepth scatter ---
        ax3 = axes[2]
        occ_mask = debug_records['occluded'].astype(bool)

        # Filter out full-screen entries for clearer visualization
        valid = ~((debug_records['minUv_x'] == 0) & (debug_records['minUv_y'] == 0) &
                  (debug_records['maxUv_x'] == 1) & (debug_records['maxUv_y'] == 1))

        vis_valid = valid & ~occ_mask
        occ_valid = valid & occ_mask

        if np.any(vis_valid):
            ax3.scatter(
                debug_records['nearDepth'][vis_valid],
                debug_records['hizDepth'][vis_valid],
                c='red', alpha=0.5, s=8, label=f'Visible ({int(np.sum(vis_valid))})'
            )
        if np.any(occ_valid):
            ax3.scatter(
                debug_records['nearDepth'][occ_valid],
                debug_records['hizDepth'][occ_valid],
                c='green', alpha=0.3, s=8, label=f'Occluded ({int(np.sum(occ_valid))})'
            )

        lim = [0.8, 1.02]
        ax3.plot(lim, lim, 'k--', alpha=0.3, label='nearDepth = hizDepth')
        ax3.set_xlabel("nearDepth")
        ax3.set_ylabel("hizDepth")
        ax3.set_title("Occlusion Test")
        ax3.legend(fontsize=8)
        ax3.set_xlim(lim)
        ax3.set_ylim(lim)

        plt.suptitle("HiZ Culling 2D Projections", fontsize=14, fontweight='bold')
        plt.tight_layout(rect=[0, 0, 1, 0.96])
        out1 = dump_dir / "projections.png"
        plt.savefig(str(out1), dpi=150, bbox_inches='tight')
        print(f"Saved 2D projections to {out1}")

    # ==========================================
    # Figure 2: Per-mip HiZ with AABBs
    # ==========================================
    total_panels = len(mip_data) + (1 if depth is not None else 0)
    cols = min(4, total_panels)
    rows = (total_panels + cols - 1) // cols
    fig2, axes2 = plt.subplots(rows, cols, figsize=(6 * cols, 5 * rows))
    if total_panels == 1:
        axes2 = np.array([axes2])
    axes2 = axes2.flatten()

    panel_idx = 0

    if depth is not None:
        ax = axes2[panel_idx]
        im = ax.imshow(depth, cmap="inferno", vmin=0, vmax=1, aspect='auto')
        ax.set_title(f"Depth ({depth.shape[1]}×{depth.shape[0]})", fontsize=10)
        plt.colorbar(im, ax=ax, shrink=0.7)
        panel_idx += 1

    mip_axes = {}
    for i, data in enumerate(mip_data):
        ax = axes2[panel_idx]
        mip_axes[i] = ax
        clear_pct = np.mean(data >= 0.999) * 100
        im = ax.imshow(data, cmap="inferno", vmin=0, vmax=1, aspect='auto')
        ax.set_title(f"Mip {i} ({data.shape[1]}×{data.shape[0]}) clr={clear_pct:.0f}%", fontsize=9)
        plt.colorbar(im, ax=ax, shrink=0.7)
        panel_idx += 1

    for i in range(panel_idx, len(axes2)):
        axes2[i].set_visible(False)

    # Draw AABBs on corresponding mip panels
    if len(debug_records) > 0:
        for item in debug_records:
            mip = int(item['mipLevel'])
            if mip not in mip_axes:
                continue
            ax = mip_axes[mip]
            mh, mw = mip_data[mip].shape

            x0 = float(item['minUv_x']) * mw
            y0 = float(item['minUv_y']) * mh
            x1 = float(item['maxUv_x']) * mw
            y1 = float(item['maxUv_y']) * mh
            rw = max(0.5, x1 - x0)
            rh = max(0.5, y1 - y0)

            is_occ = bool(item['occluded'])
            color = '#00ff00' if is_occ else '#ff3333'
            alpha = 0.3 if is_occ else 0.7
            lw = 0.5 if is_occ else 1.0

            rect = patches.Rectangle(
                (x0, y0), rw, rh,
                linewidth=lw, edgecolor=color, facecolor='none', alpha=alpha
            )
            ax.add_patch(rect)

    plt.suptitle("HiZ Mip Chain + Culling AABBs", fontsize=13, fontweight='bold')
    plt.tight_layout(rect=[0, 0, 1, 0.96])
    out2 = dump_dir / "mip_levels.png"
    plt.savefig(str(out2), dpi=150, bbox_inches='tight')
    print(f"Saved mip visualization to {out2}")

    # Contamination summary
    if mip_data:
        print("\n=== 1.0-Contamination per Mip ===")
        for i, data in enumerate(mip_data):
            pct = np.mean(data >= 0.999) * 100
            bar = "█" * int(pct) + "░" * (50 - int(pct))
            print(f"  Mip {i:2d}: {pct:5.1f}%  {bar[:50]}")

    plt.show()


if __name__ == "__main__":
    main()
