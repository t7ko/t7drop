#!/usr/bin/env python3
"""t7drop command-line client; see t7drop_cli/main.py, or run with --help."""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from t7drop_cli.main import main  # noqa: E402

if __name__ == "__main__":
    sys.exit(main())
