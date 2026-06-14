# CAN Variable Monitor

Windows upper-computer tool for monitoring Keil firmware variables through CAN.

## Features

- Auto-parse Keil ARM `.map` files.
- Case-insensitive variable search.
- Fuzzy matching for variable names.
- Save and load monitor variable lists.
- Monitor up to 100 variables.
- Supports `Mock`, `广成GC`, and `SYS` adapters.
- Uses request/response CAN IDs, default `0x7F0` and `0x7F1`.

## Usage

1. Build the controller firmware and generate a `.map` file.
2. Add the CAN monitor firmware agent from `CAN_MONITOR_PROTOCOL.md`.
3. Run `run_can_variable_monitor.bat`.
4. Select the map file.
5. Type a variable name, double-click a fuzzy match, or press Enter to add it.
6. Connect the CAN adapter and click `开始监控`.

Structure fields are not listed by map files. For byte-offset reads, type:

```text
gRunInfo+19
gSysInfo+2
```

