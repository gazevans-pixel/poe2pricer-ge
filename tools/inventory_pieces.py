#!/usr/bin/env python3
"""Stücke des Inventar-Panels aus den Spieldateien nach data/ui holen.

Das Programm fragt Preise für Waystones und Tablets nur noch, solange das Inventar-Panel des
Spiels auf dem Bildschirm steht (Inventar, Truhe, Händler, Kartengerät zeigen es rechts). Der
Tooltip der Bodenbeute, den die Controller-Auswahl beim Vorbeilaufen einblendet, steht ohne
Panel und bekommt so keinen Preis (OFFEN U23, 08.11.2026).

Das Panel ist die Grafik `Art/2DArt/UIImages/InGame/InventoryPanel/InventoryPanelUpperBackground`
(970×754). Mit der Tastatur zeichnet das Spiel sie bei 1440 Bildhöhe im Maßstab 0,90, am
Controller ungleich gedehnt, 0,935 waagerecht und 0,965 senkrecht (gemessen 08.11.2026 an den
Stash-Fixtures: Tastatur Spanne der Stücke 0–1 px bei .99, Controller 2 px bei .89–.90).
Vorlagen sind Ausschnitte, in denen keine Gegenstände liegen (die Statuen links und rechts und
die Figurengruppen unten); der Erkenner (InventoryPanel) skaliert sie je Bedienart und nimmt das
Panel an, wenn zwei Stücke auf denselben Ursprung zeigen. Gespeichert werden die Ausschnitte in
Originalgröße, über Schwarz gelegt und grau, so wie der Erkenner sie vergleicht.

Dieses Skript läuft nur bei uns und nur, wenn Grinding Gear Games die Grafik ändert. Das
Programm selbst liest nur die fertigen PNG-Dateien.

Voraussetzungen: die Werkzeugkiste (~/.claude/tools/poe-dat/poe-dat.mjs, liest die Bundles der
Steam-Installation) und Pillow (`python3 -m pip install --user pillow`).

Aufruf (aus WSL, im Projektordner):
  tools/inventory_pieces.py [--spiel <Spielordner>] [--ziel data/ui]

Die Bilder sind Material von Grinding Gear Games.
"""
import argparse
import os
import subprocess
import sys
import tempfile

from PIL import Image

ART = "Art/2DArt/UIImages/InGame/InventoryPanel/InventoryPanelUpperBackground"
TOOL = os.path.expanduser("~/.claude/tools/poe-dat/poe-dat.mjs")
# Name → Ausschnitt (x, y, Breite, Höhe) im Art; die Lage steht ebenso in Tuning.Inventory.
PIECES = {
    "right-statue": (790, 420, 170, 200),
    "left-statue": (0, 470, 160, 180),
    "bottom-left": (60, 620, 190, 134),
    "bottom-right": (760, 640, 200, 114),
}


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--spiel", help="Spielordner (sonst POE2_DIR oder die Steam-Pfade)")
    parser.add_argument("--ziel", default="data/ui")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="poe2pricer-inventar-") as work:
        command = ["node", TOOL, "dateien", work, ART]
        if args.spiel:
            command += ["--spiel", args.spiel]
        done = subprocess.run(command, capture_output=True, text=True)
        if done.returncode != 0:
            sys.exit("poe-dat: " + (done.stderr or done.stdout).strip())
        art = Image.open(os.path.join(work, ART + ".png")).convert("RGBA")
        if art.size != (970, 754):
            sys.exit(f"{ART}: {art.size[0]}x{art.size[1]} statt 970x754 — Maße in Tuning.Inventory prüfen")
        ground = Image.new("RGBA", art.size, (0, 0, 0, 255))
        ground.alpha_composite(art)
        gray = ground.convert("L")
        os.makedirs(args.ziel, exist_ok=True)
        for name, (x, y, w, h) in PIECES.items():
            path = os.path.join(args.ziel, f"inventory-{name}.png")
            gray.crop((x, y, x + w, y + h)).save(path)
            print(f"{path}: {w}x{h} aus ({x},{y})")


if __name__ == "__main__":
    main()
