# WFInfo OCR Test Framework

Regression and accuracy testing for WFInfo's OCR pipeline. Runs headlessly from the command line using the real WFInfo.Core OCR methods (no mocks).

## How It Works

1. The runner reads `map.json` which lists scenario paths.
2. Each scenario is a **PNG + JSON pair** (e.g. `data/test1.png` + `data/test1.json`).
3. The JSON spec defines language, theme, scaling, category, and expected part names.
4. WFInfo.Core's OCR pipeline processes the screenshot via SkiaSharp + Tesseract.
5. Actual results are compared against expected parts; accuracy and pass/fail are reported.

## Directory Structure

```text
tests/
├── map.json           # Lists scenarios to run
├── run_tests.sh       # One-click Linux runner
├── benchmark_ocr.sh   # Before/after benchmark comparison
├── core/              # .NET test runner project
│   ├── CoreOcrTests.csproj
│   └── Program.cs
├── data/
│   ├── test1.json     # Test spec
│   ├── test1.png      # Corresponding screenshot
│   └── ...
```

## Quick Start

```bash
cd tests
./run_tests.sh
```

Or manually:

```bash
dotnet run --project tests/core -c Release -- tests/map.json results.json
```

If no output file is specified, results go to `test_results_<timestamp>.json`.

The first run downloads market data from the warframestat.us API.

## Benchmarking

Compare OCR results and timing before/after a change:

```bash
cd tests
./benchmark_ocr.sh baseline      # save baseline
# ... make changes ...
./benchmark_ocr.sh after-fix     # auto-compares with last run
```

## Test Spec Format (JSON)

Each test scenario JSON file:

```json
{
  "description": "Basic English reward screen with 4 items",
  "resolution": "1920x1080",
  "scaling": 100,
  "theme": "orokin",
  "language": "english",
  "parts": {
    "0": "Volt Prime Blueprint",
    "1": "Mag Prime Blueprint",
    "2": "Ash Prime Blueprint",
    "3": "Trinity Prime Blueprint"
  },
  "category": "reward",
  "hdr": false,
  "filters": []
}
```

### Fields

| Field | Required | Description |
|-------|----------|-------------|
| `description` | No | Human-readable description |
| `resolution` | No | Source resolution (informational) |
| `scaling` | Yes | UI scaling percentage (100 = 100%) |
| `theme` | Yes | UI theme name (see below) |
| `language` | Yes | Language name (see below) |
| `parts` | Yes | Map of index to expected part name (English) |
| `category` | Yes | `reward` or `snapit` |
| `hdr` | Yes | Whether the screenshot is HDR |
| `filters` | No | Optional filter tags (e.g. `colorblind`) |

## map.json Format

```json
{
  "scenarios": [
    "data/test1",
    "data/test2",
    "data/test3"
  ]
}
```

Each entry is a path (relative to `map.json`) without extension. The runner appends `.json` and `.png`.

## Supported Values

### Categories
- **`reward`** - Fissure reward screen (1-4 items)
- **`snapit`** - SnapIt inventory scanning

### Languages
`english`, `korean`, `japanese`, `simplified chinese`, `traditional chinese`, `thai`, `french`, `ukrainian`, `italian`, `german`, `spanish`, `portuguese`, `polish`, `turkish`, `russian`

### Themes
`orokin`, `tenno`, `grineer`, `corpus`, `infested`, `lotus`, `fortuna`, `baruuk`, `equinox`, `dark lotus` / `dark_lotus`, `zephyr`, `high contrast` / `high_contrast`, `legacy`, `vitruvian`, `stalker`, `conquera`, `deadlock`, `lunar renewal` / `lunar_renewal`, `pom 2` / `pom_2`, `auto`

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | All tests passed |
| 1 | Some tests failed |
| 2 | Fatal error (missing files, init failure, etc.) |

## Adding New Tests

1. Take a screenshot in Warframe
2. Save as `tests/data/<name>.png`
3. Create `tests/data/<name>.json` with the spec (see format above)
4. Add `"data/<name>"` to `map.json` scenarios list
5. Run `./run_tests.sh`

## Troubleshooting

- **"Databases not ready"** - First run downloads market data from the internet. Ensure connectivity.
- **"PNG not found"** - The `.png` must be next to the `.json` with the same base name.
- **Low accuracy** - Check that expected part names match WFInfo's English database names exactly.
- **Tesseract errors** - Ensure tessdata files are available in `~/.local/share/WFInfo/tessdata/`.
- **Debug logs** - Check `~/.local/share/WFInfo/debug.log` for detailed OCR pipeline logs.
