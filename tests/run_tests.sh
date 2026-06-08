#!/bin/bash
# WFInfo Core OCR Test Runner (Linux)
# Runs the same test data as WPF but through Core's SkiaSharp OCR pipeline.
#
# Usage: cd tests && ./run_tests.sh

set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR/.."

echo "WFInfo Core OCR Test Runner"
echo "========================"
echo

TIMESTAMP=$(date +%Y%m%d_%H%M%S)
OUTPUT="${SCRIPT_DIR}/test_results_${TIMESTAMP}.json"

echo "Building..."
dotnet build tests/core/CoreOcrTests.csproj -c Release --nologo -v q

echo
dotnet run --project tests/core -c Release -- "${SCRIPT_DIR}/map.json" "$OUTPUT"
EXIT_CODE=$?

echo
if [ $EXIT_CODE -eq 0 ]; then
    echo "All tests passed!"
elif [ $EXIT_CODE -eq 1 ]; then
    echo "Some tests failed. Check results for details."
else
    echo "Test execution encountered an error."
fi

exit $EXIT_CODE