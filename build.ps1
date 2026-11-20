$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319'
if (!(Test-Path $framework)) { $framework = Join-Path $env:windir 'Microsoft.NET\Framework\v4.0.30319' }
$refs = 'PresentationCore','PresentationFramework','WindowsBase' | ForEach-Object { "/reference:$framework\WPF\$_.dll" }
& "$framework\csc.exe" /nologo /target:winexe "/win32icon:$PSScriptRoot\EOpen.ico" /optimize+ "/out:$PSScriptRoot\EOpen.exe" @refs /reference:System.Xaml.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "$PSScriptRoot\EOpen.cs" "$PSScriptRoot\RegionCapture.cs" "$PSScriptRoot\PhoneTransfer.cs" "/reference:$PSScriptRoot\QRCoder.dll" "/resource:$PSScriptRoot\phone.html,phone.html"
exit $LASTEXITCODE


