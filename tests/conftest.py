"""Shared pytest configuration for the ECI-CAS suites."""
from __future__ import annotations

import os

import pytest


def pytest_configure(config):
    config.addinivalue_line(
        "markers",
        "live: hits a real LLM endpoint. Costs money and is non-deterministic. "
        "Skipped unless ECI_LIVE_TESTS=1 and the substrate's credentials are set.",
    )
    config.addinivalue_line(
        "markers",
        "calibration: a live test that asserts the model's JUDGMENT rather than "
        "the mechanism. A failure here is usually a prompt bug, not a code bug — "
        "run with -s and read what the model actually said before changing "
        "anything. Deselect with -m 'live and not calibration'.",
    )


def pytest_collection_modifyitems(config, items):
    if os.environ.get("ECI_LIVE_TESTS") == "1":
        return
    skip_live = pytest.mark.skip(
        reason="live substrate tests are opt-in: set ECI_LIVE_TESTS=1 (and the "
               "provider's API key) to run them"
    )
    for item in items:
        if "live" in item.keywords:
            item.add_marker(skip_live)
