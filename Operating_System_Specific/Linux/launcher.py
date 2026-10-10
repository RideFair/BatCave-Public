"""Installed entry point; -I prevents user Python paths replacing application code."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from batcave_linux.__main__ import main

raise SystemExit(main())
