# GeminiCapper
# Copyright 2026 Yasir Mo (https://github.com/yasir-mo). Apache License 2.0.
# Blocks Google Antigravity & Gemini CLI requests when quota/rate limits reach threshold.

param([int]$CacheSeconds = 60)
$ErrorActionPreference = 'Stop'

try {
    $threshold = 90
    $pausedUntilEpoch = 0
    $pacingEnabled = $false
    $pointsPerDay = 14.3

    $toolDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $configFile = Join-Path $toolDir 'config.json'
    if (Test-Path $configFile) {
        try {
            $cfg = Get-Content $configFile -Raw | ConvertFrom-Json
            if ($null -ne $cfg.threshold) { $threshold = [double]$cfg.threshold }
            if ($null -ne $cfg.pausedUntilEpoch) { $pausedUntilEpoch = [long]$cfg.pausedUntilEpoch }
            if ($null -ne $cfg.pacing) {
                if ($null -ne $cfg.pacing.enabled) { $pacingEnabled = [bool]$cfg.pacing.enabled }
                if ($null -ne $cfg.pacing.pointsPerDay) { $pointsPerDay = [double]$cfg.pacing.pointsPerDay }
            }
        } catch {}
    }

    $nowEpoch = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    if ($pausedUntilEpoch -eq -1 -or $pausedUntilEpoch -gt $nowEpoch) { exit 0 }

    # Load usage
    $cacheFile = Join-Path $env:USERPROFILE '.gemini\usage_cache.json'
    if (Test-Path $cacheFile) {
        $data = Get-Content $cacheFile -Raw | ConvertFrom-Json
        foreach ($limit in $data.limits) {
            if ($limit.percent -ge $threshold) {
                [Console]::Error.WriteLine("BLOCKED by GeminiCapper: $($limit.name) is at $($limit.percent)% (threshold $threshold%). Open GeminiCapper app to pause.")
                exit 2
            }
        }
    }
    exit 0
} catch {
    exit 0
}
