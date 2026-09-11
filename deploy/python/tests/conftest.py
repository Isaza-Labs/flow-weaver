"""Pytest config: put deploy/python on sys.path so tests can import the
runner modules the same way the Dockerfile does (PYTHONPATH=/usr/local/lib).
"""
import sys
from pathlib import Path

DEPLOY_PY = Path(__file__).resolve().parent.parent
if str(DEPLOY_PY) not in sys.path:
    sys.path.insert(0, str(DEPLOY_PY))
