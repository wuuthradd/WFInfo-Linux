#!/bin/bash
# WFInfo OCR Benchmark - run before/after a change to compare results + timing.
#
# Usage:
#   cd tests && ./benchmark_ocr.sh [label]
#
# Examples:
#   ./benchmark_ocr.sh baseline        # save as "baseline"
#   ./benchmark_ocr.sh after-fix       # save as "after-fix", auto-compare with last run
#
# Results are saved to tests/bench_<label>.json
# If a previous run exists, differences are shown automatically.

set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR/.."

LABEL="${1:-run_$(date +%H%M%S)}"
OUTPUT="${SCRIPT_DIR}/bench_${LABEL}.json"
LAST_LINK="${SCRIPT_DIR}/bench_last.json"

# Check test data exists
MAP="${SCRIPT_DIR}/map.json"
if [ ! -f "$MAP" ]; then
    echo "ERROR: map.json not found at $MAP"
    exit 2
fi

# Check at least one test PNG exists
HAS_PNG=0
for scenario in $(python3 -c "import json; [print(s) for s in json.load(open('$MAP'))['scenarios']]" 2>/dev/null); do
    if [ -f "${SCRIPT_DIR}/${scenario}.png" ]; then
        HAS_PNG=1
        break
    fi
done

if [ $HAS_PNG -eq 0 ]; then
    echo "ERROR: No test PNG images found in tests/data/"
    echo ""
    echo "To create test data, take a Warframe reward screenshot and save it as:"
    echo "  tests/data/test1.png  (matching tests/data/test1.json)"
    echo ""
    echo "The PNG should be a full-screen screenshot showing the reward selection screen."
    exit 2
fi

echo "═══ WFInfo OCR Benchmark [$LABEL] ═══"
echo ""

# Build
echo "Building..."
dotnet build tests/core/CoreOcrTests.csproj -c Release --nologo -v q 2>&1 | tail -3
echo ""

# Run with timing
START=$(date +%s%N)
dotnet run --project tests/core -c Release -- "$MAP" "$OUTPUT"
EXIT_CODE=$?
END=$(date +%s%N)

ELAPSED_MS=$(( (END - START) / 1000000 ))
echo ""
echo "Total wall time: ${ELAPSED_MS}ms"
echo "Results: $OUTPUT"

# Compare with previous run if exists
if [ -f "$LAST_LINK" ] && [ "$OUTPUT" != "$(readlink -f "$LAST_LINK" 2>/dev/null)" ]; then
    PREV="$LAST_LINK"
    echo ""
    echo "── Comparing with previous run ──"
    python3 -c "
import json, sys

prev = json.load(open('$PREV'))
curr = json.load(open('$OUTPUT'))

# Compare accuracy
prev_acc = prev.get('OverallAccuracy', 0)
curr_acc = curr.get('OverallAccuracy', 0)
diff_acc = curr_acc - prev_acc

# Compare timing
prev_tests = {t['TestCaseName']: t for t in prev.get('TestResults', [])}
curr_tests = {t['TestCaseName']: t for t in curr.get('TestResults', [])}

print(f'  Accuracy: {prev_acc:.1f}% -> {curr_acc:.1f}% ({diff_acc:+.1f}%)')

all_names = sorted(set(list(prev_tests.keys()) + list(curr_tests.keys())))
changed = False
for name in all_names:
    p = prev_tests.get(name)
    c = curr_tests.get(name)
    if not p or not c:
        continue
    p_ms = p.get('ProcessingTimeMs', 0)
    c_ms = c.get('ProcessingTimeMs', 0)
    p_parts = p.get('ActualParts', [])
    c_parts = c.get('ActualParts', [])
    
    time_diff = c_ms - p_ms
    if p_parts != c_parts:
        changed = True
        print(f'  CHANGED {name}: {p_parts} -> {c_parts}')
    if abs(time_diff) > 50:
        print(f'  {name}: {p_ms}ms -> {c_ms}ms ({time_diff:+d}ms)')

if not changed:
    print('  OCR results: IDENTICAL (no regressions)')
" 2>/dev/null || echo "  (python3 needed for comparison)"
fi

# Update last-run link
cp "$OUTPUT" "$LAST_LINK"

exit $EXIT_CODE