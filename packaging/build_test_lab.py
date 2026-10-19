"""Build a self-contained Test Lab bundle from this repository only."""
from pathlib import Path
import runpy
root = Path(__file__).resolve().parents[1]
runpy.run_path(str(root / "dependencies/test-lab/build_bundle.py"))["build"](root)
