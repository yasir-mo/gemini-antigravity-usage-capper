# GeminiCapper: Google Antigravity & Gemini Usage Tracker and Capper

Tracks and caps Google Antigravity and Gemini CLI usage before quota, RPM/TPM, and daily request limits are exhausted.

## Features
- Hooks Protection: Halts Antigravity/Gemini agent loops before quota overruns occur.
- System Tray Background Persistence: Stays active in the Windows notification area when minimized or closed.
- Visual Quota Meters: Live monitoring of Gemini and Claude shared quota pools with real-time usage percentages and reset countdowns.
- Daily Pacing: Spreads weekly/monthly quota evenly across days.
- Standalone Binary: GeminiCapper.exe runs on any Windows machine with zero runtime installation.

## Setup
Add the hook to your Antigravity / Gemini configuration or run GeminiCapper.exe to configure thresholds.

## License
Licensed under Apache License 2.0. Copyright 2026 [Yasir Mo](https://github.com/yasir-mo).
