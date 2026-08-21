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

    # Query live quota from Windows Credential Manager & Google Cloud Code Assist API
    $usageCacheFile = Join-Path $env:TEMP "gemini_capper_quota_cache.json"
    $quotaData = $null
    
    if (Test-Path $usageCacheFile) {
        $cacheItem = Get-Item $usageCacheFile
        $ageSec = ((Get-Date) - $cacheItem.LastWriteTime).TotalSeconds
        if ($ageSec -lt $CacheSeconds) {
            try {
                $quotaData = Get-Content $usageCacheFile -Raw | ConvertFrom-Json
            } catch {}
        }
    }

    if (-not $quotaData) {
        $credReaderCode = @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public class CapperCredReader {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CREDENTIAL {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
    public static extern void CredFree(IntPtr cred);

    public static string Read(string target) {
        IntPtr credPtr;
        if (CredRead(target, 1, 0, out credPtr)) {
            try {
                CREDENTIAL cred = (CREDENTIAL)Marshal.PtrToStructure(credPtr, typeof(CREDENTIAL));
                byte[] b = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, b, 0, cred.CredentialBlobSize);
                return Encoding.UTF8.GetString(b);
            } finally {
                CredFree(credPtr);
            }
        }
        return null;
    }
}
"@
        if (-not ([System.Management.Automation.PSTypeName]'CapperCredReader').Type) {
            Add-Type -TypeDefinition $credReaderCode -Language CSharp
        }

        $credJson = [CapperCredReader]::Read("gemini:antigravity")
        if ($credJson) {
            $authData = $credJson | ConvertFrom-Json
            $token = $authData.token.access_token
            if ($token) {
                $headers = @{
                    "Authorization" = "Bearer $token"
                    "Content-Type" = "application/json"
                    "User-Agent" = "antigravity/1.0"
                }
                $res = Invoke-RestMethod -Uri "https://daily-cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels" -Method Post -Headers $headers -Body "{}" -TimeoutSec 5
                if ($res -and $res.models) {
                    $quotaData = $res
                    try {
                        $res | ConvertTo-Json -Depth 5 | Set-Content $usageCacheFile -Force
                    } catch {}
                }
            }
        }
    }

    # Evaluate models and check if any exceed threshold
    if ($quotaData -and $quotaData.models) {
        foreach ($prop in $quotaData.models.psobject.Properties) {
            $model = $prop.Value
            $name = if ($model.displayName) { $model.displayName } else { $prop.Name }
            if ($model.quotaInfo -and $null -ne $model.quotaInfo.remainingFraction) {
                $usedPct = [math]::Round((1.0 - [double]$model.quotaInfo.remainingFraction) * 100, 1)
                if ($usedPct -ge $threshold) {
                    [Console]::Error.WriteLine("BLOCKED by GeminiCapper: $name usage is at $usedPct% (threshold $threshold%). Open GeminiCapper app to pause.")
                    exit 2
                }
            }
        }
    }
    exit 0
} catch {
    exit 0
}
