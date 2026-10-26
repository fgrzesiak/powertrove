# Manual Excel Test Protocol

ContextBar uses a COM adapter to retrieve the selected range in Microsoft Excel on Windows. Because COM interactions depend heavily on the local Windows/Office environment, some behaviors cannot be fully verified via unit tests on Linux.

Please perform the following manual tests on a Windows machine with Microsoft Excel installed.

## Prerequisites
- A Windows machine.
- Microsoft Excel installed (Desktop version).
- ContextBar built and running.

## Test Cases

### 1. Simple Single Cell
- **Action**: Open Excel. Type "Test" in cell A1. Select cell A1. Press the ContextBar hotkey.
- **Expected**: ContextBar should capture a 1x1 table containing "Test" and display appropriate actions (e.g., Export to JSON).

### 2. Rectangular Range (Multiple Types)
- **Action**: In Excel, create a 2x2 grid containing a string, a number, a boolean (`TRUE`), and an empty cell. Select the entire 2x2 range. Press the ContextBar hotkey.
- **Expected**: ContextBar should capture a 2x2 table. Previews of Export to JSON should correctly reflect string, number, boolean, and null values.

### 3. Unsupported: Charts/Shapes
- **Action**: Insert a Chart or a Shape in Excel. Select the Chart/Shape. Press the ContextBar hotkey.
- **Expected**: ContextBar should display an "Unsupported" state indicating that the selected item is not a range.

### 4. Unsupported: Disjoint Selection
- **Action**: Select cell A1, hold Ctrl, and select cell B2. Press the ContextBar hotkey.
- **Expected**: ContextBar should display an "Unsupported" state indicating disjoint selections are not supported.

### 5. Large Range Limit
- **Action**: Select the entire column A (over 1 million cells). Press the ContextBar hotkey.
- **Expected**: ContextBar should gracefully decline, showing an "Unsupported" message that the range is too large, rather than hanging the application indefinitely.

### 6. Excel Not Running
- **Action**: Close all Excel instances. While focused on another application, press the ContextBar hotkey.
- **Expected**: ContextBar should not hang. The Excel context provider should gracefully skip capture.
