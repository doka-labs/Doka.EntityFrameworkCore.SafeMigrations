#!/usr/bin/env python3
"""Require a complete, unskipped SQL Server live-test run."""

import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def verify(path: Path) -> None:
    """Reject missing, skipped, failed, or incomplete TRX evidence."""

    root = ET.parse(path).getroot()
    counters = root.find(".//{*}Counters")
    results = root.findall(".//{*}UnitTestResult")

    if counters is None:
        raise ValueError("SQL Server qualification has no TRX counters.")

    total = int(counters.get("total", "0"))

    if total == 0:
        raise ValueError("SQL Server qualification produced no test results.")

    if len(results) != total:
        raise ValueError("SQL Server qualification has incomplete TRX results.")

    if any(result.get("outcome") != "Passed" for result in results):
        raise ValueError("SQL Server qualification contains skipped or failed tests.")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: verify-sqlserver-trx.py <trx-path>")

    try:
        verify(Path(sys.argv[1]))
    except (OSError, ValueError, ET.ParseError) as error:
        raise SystemExit(str(error)) from error
