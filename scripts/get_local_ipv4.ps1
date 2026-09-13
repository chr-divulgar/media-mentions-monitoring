$ErrorActionPreference = 'Stop'

function Select-ValidIp {
    param(
        [Parameter(ValueFromPipeline = $true)]
        [object[]]$Items
    )

    process {
        foreach ($item in $Items) {
            if ($null -eq $item) {
                continue
            }

            $ip = $item.IPAddress
            if ([string]::IsNullOrWhiteSpace($ip)) {
                continue
            }

            if ($ip -eq '127.0.0.1' -or $ip -like '169.254.*') {
                continue
            }

            return $ip
        }
    }
}

# 1) Preferred source: interface used by the default route.
$route = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
    Sort-Object -Property RouteMetric, InterfaceMetric |
    Select-Object -First 1

if ($route) {
    $ipFromDefaultRoute = Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $route.InterfaceIndex -ErrorAction SilentlyContinue |
        Where-Object { $_.AddressState -eq 'Preferred' } |
        Select-Object -First 1 |
        Select-ValidIp

    if ($ipFromDefaultRoute) {
        Write-Output $ipFromDefaultRoute
        exit 0
    }
}

# 2) Fallback: any preferred non-loopback/non-APIPA IPv4.
$anyPreferredIp = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.AddressState -eq 'Preferred' } |
    Select-ValidIp

if ($anyPreferredIp) {
    Write-Output $anyPreferredIp
    exit 0
}

exit 1
