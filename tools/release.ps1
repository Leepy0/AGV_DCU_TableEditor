<#
배포 묶음을 만든다(GitHub Release 는 만들지 않는다).
  tools\release.ps1 -Version v0.1.0 [-Gcc <mingw64 폴더>] [-Out publish] [-AppRepo <앱 저장소>] [-Site <사이트 폴더 이름>]
차례: 1 빌드·시험  2 게시(두 실행 파일을 한 폴더)  3 휴대용 gcc 추리기  4 라이선스·출처  5 묶음 시험  6 VERSION·zip·해시
-AppRepo 가 없으면 5 의 (나)(다)는 건너뛴다. -Site 는 -AppRepo 와 같이 준다.
#>
param(
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+.*$')][string]$Version,
    [string]$Gcc,
    [string]$Out = 'publish',
    [string]$AppRepo,
    [string]$Site
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if ($AppRepo -and -not $Site) { throw '-AppRepo 를 주면 -Site(사이트 폴더 이름)도 준다' }

# ---- 휴대용 gcc 에 넣을 것(한 곳). 상대 경로는 mingw64 폴더 기준. 모자라면 여기에 더한다 ----
$T = 'x86_64-w64-mingw32'
$V = $null   # gcc 버전 폴더 이름(아래에서 정함)
$dllsGcc = 'libiconv-2.dll', 'libwinpthread-1.dll'
$dllsBin = 'libintl-8.dll', 'zlib1.dll', 'libzstd.dll', 'libwinpthread-1.dll', 'libiconv-2.dll'
$dllsCc1 = 'libgmp-10.dll', 'libmpfr-6.dll', 'libmpc-3.dll', 'libisl-23.dll', 'libiconv-2.dll', 'libzstd.dll', 'zlib1.dll', 'libwinpthread-1.dll', 'libgcc_s_seh-1.dll'
$importLibs = 'mingw32', 'mingwex', 'moldname', 'msvcrt', 'ucrt', 'ucrtbase', 'kernel32', 'advapi32', 'shell32', 'user32', 'gdi32',
    'ws2_32', 'wsock32', 'winmm', 'ole32', 'oleaut32', 'uuid', 'comdlg32', 'comctl32', 'shlwapi', 'psapi', 'bcrypt', 'ntdll', 'm', 'pthread', 'winpthread',
    'mingwthrd', 'gmon', 'imagehlp', 'dbghelp', 'version', 'iphlpapi', 'setupapi', 'secur32', 'crypt32', 'userenv', 'shcore', 'mswsock'
function GccParts {
    param($g)
    # (원본 상대 경로, 묶음 상대 경로, 글 패턴 또는 $null=폴더 통째, 뺄 하위 폴더)
    @(
        @("bin", "bin", (@('gcc.exe') + $dllsGcc), $null),
        @("$T\bin", "$T\bin", (@('as.exe', 'ld.exe') + $dllsBin), $null),
        @("libexec\gcc\$T\$V", "libexec\gcc\$T\$V", (@('cc1.exe', 'collect2.exe', 'liblto_plugin.dll') + $dllsCc1), $null),
        @("lib\gcc\$T\$V", "lib\gcc\$T\$V", @('crtbegin.o', 'crtend.o', 'libgcc.a', 'libgcc_eh.a'), $null),
        @("lib\gcc\$T\$V\include", "lib\gcc\$T\$V\include", $null, @()),
        @("lib\gcc\$T\$V\include-fixed", "lib\gcc\$T\$V\include-fixed", $null, @()),
        @("$T\lib", "$T\lib", (@('*.o') + ($importLibs | ForEach-Object { "lib$_.a" })), $null),
        @("$T\include", "$T\include", $null, @('c++', 'ddk', '*.idl'))
    )
}
# ----------------------------------------------------------------------------------------------

function Step($n, $t) { Write-Host "`n== $n. $t" -ForegroundColor Cyan }
function Native($exe, [string[]]$a) { & $exe @a; if ($LASTEXITCODE -ne 0) { throw "$exe $($a -join ' ') 종료 코드 $LASTEXITCODE" } }
function Size($p) { '{0:N1} MB' -f ((Get-ChildItem $p -Recurse -File | Measure-Object Length -Sum).Sum / 1MB) }
$sw = [Diagnostics.Stopwatch]::StartNew()
$num = $Version.TrimStart('v')
$name = "TableEditor-$Version-win-x64"
$outFull = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
$pack = Join-Path $outFull $name
$zip = "$pack.zip"

# gcc 폴더 찾기
if (-not $Gcc) {
    $g = (where.exe gcc 2>$null | Select-Object -First 1)
    if (-not $g) { throw 'gcc 를 못 찾음: -Gcc <mingw64 폴더> 를 준다' }
    $Gcc = Split-Path -Parent (Split-Path -Parent $g)
}
if (-not (Test-Path (Join-Path $Gcc 'bin\gcc.exe'))) { throw "gcc 폴더가 아님: bin\gcc.exe 없음" }
$V = (Get-ChildItem (Join-Path $Gcc "lib\gcc\$T") -Directory | Select-Object -First 1).Name

Step 1 '빌드·시험'
Native dotnet @('build', '-c', 'Release', '-nologo', '-warnaserror')   # 경고도 실패
$testExe = Get-ChildItem (Join-Path $root 'tests\TableEditor.Tests\bin\Release') -Filter TableEditor.Tests.exe -Recurse | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $testExe) { throw '시험 실행 파일 없음' }
Native $testExe.FullName @()
Write-Host '경고 0, 시험 통과'

Step 2 '게시(한 폴더)'
if (Test-Path $pack) { Remove-Item $pack -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
foreach ($p in 'TableEditor.App', 'TableEditor.Cli') {
    Native dotnet @('publish', "src\$p", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', "-p:Version=$num", '-p:DebugType=none', '-p:DebugSymbols=false', '-o', $pack, '-nologo')
}
Get-ChildItem $pack -Recurse -Filter *.pdb | Remove-Item -Force
foreach ($f in 'TableEditor.exe', 'tedit.exe') { if (-not (Test-Path (Join-Path $pack $f))) { throw "$f 없음" } }

Step 3 '휴대용 gcc 추리기'
$gdst = Join-Path $pack 'gcc'
foreach ($part in (GccParts $Gcc)) {
    $src = Join-Path $Gcc $part[0]; $dst = Join-Path $gdst $part[1]
    if (-not (Test-Path $src)) { Write-Warning "없음: $($part[0])"; continue }
    New-Item -ItemType Directory -Force $dst | Out-Null
    if ($part[2]) {
        foreach ($pat in $part[2]) {
            $hit = Get-ChildItem $src -File -Filter $pat
            if (-not $hit) { Write-Warning "없음: $($part[0])\$pat" }
            $hit | Copy-Item -Destination $dst -Force
        }
    } else {
        Get-ChildItem $src | Copy-Item -Destination $dst -Recurse -Force
        foreach ($x in @($part[3])) { Get-ChildItem $dst -Recurse -Force -Filter $x | Remove-Item -Recurse -Force }
    }
}

Step 4 '라이선스·출처'
$lic = Join-Path $gdst 'licenses'
New-Item -ItemType Directory -Force $lic | Out-Null
$licFiles = @()
foreach ($d in $Gcc, (Split-Path -Parent $Gcc)) {
    $licFiles += Get-ChildItem $d -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(COPYING|LICENSE|LICENCE|NOTICE|licenses?)' }
    $ld = Join-Path $d 'licenses'
    if (Test-Path $ld) { Copy-Item "$ld\*" $lic -Recurse -Force }
}
$licFiles | Copy-Item -Destination $lic -Force
$verFile = Join-Path $Gcc 'version_info.txt'
$verText = ''
if (Test-Path $verFile) {
    Copy-Item $verFile $lic -Force   # 배포판이 붙인 버전·구성 글
    $verText = [IO.File]::ReadAllText($verFile).Trim()
}
$gccLine = (& (Join-Path $Gcc 'bin\gcc.exe') --version | Select-Object -First 1)
$src = @"
휴대용 gcc 출처
===============
배포판: WinLibs (Brecht Sanders), MinGW-w64 UCRT / POSIX 스레드 / SEH 빌드
gcc: $gccLine

내려받은 곳:
  https://winlibs.com/
  https://github.com/brechtsanders/winlibs_mingw/releases
소스: 위 두 곳과 각 구성 요소(GCC, binutils, MinGW-w64)의 공식 소스 배포처에 있다.
이 폴더는 C 컴파일·링크에 필요한 파일만 추린 것이다(C++, Fortran, gdb, 문서, 로캘 등은 뺐다).
각 구성 요소의 라이선스(GCC 는 GPL-3.0 과 GCC Runtime Library Exception, binutils 는 GPL-3.0, MinGW-w64 런타임은 ZPL-2.1/공개 도메인 등)는 배포판 기준이다.

배포판 버전 글(version_info.txt):
------------------------------------------------
$verText
"@
[IO.File]::WriteAllText((Join-Path $gdst 'SOURCE.txt'), ($src -replace "`r?`n", "`r`n"), (New-Object Text.UTF8Encoding $true))
Write-Host "라이선스 파일 $(@(Get-ChildItem $lic -Recurse -File).Count)개: $((Get-ChildItem $lic -Recurse -File | ForEach-Object Name) -join ', ')"

Step 5 '묶음 시험'
$origPath = $env:PATH
$tmp = Join-Path $env:TEMP ("tedit_rel_" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $tmp | Out-Null
try {
    $clean = ($env:PATH -split ';' | Where-Object {
            $_ -and -not (Test-Path (Join-Path $_ 'gcc.exe') -ErrorAction SilentlyContinue) -and
            -not (Test-Path (Join-Path $_ 'cc.exe') -ErrorAction SilentlyContinue) -and
            -not (Test-Path (Join-Path $_ 'python.exe') -ErrorAction SilentlyContinue) -and
            -not (Test-Path (Join-Path $_ 'python3.exe') -ErrorAction SilentlyContinue) -and
            $_ -notmatch 'mingw|python'
        }) -join ';'
    $env:PATH = $clean
    if (Get-Command gcc -ErrorAction SilentlyContinue) { throw '시험 환경 PATH 에 gcc 가 남음' }
    $tedit = Join-Path $pack 'tedit.exe'
    $demo = Join-Path $root 'tests\fixtures\T900_DEMO'

    Write-Host '(가) roundtrip'
    Native $tedit @('roundtrip', $demo)

    if ($AppRepo) {
        $app = Join-Path $tmp 'app'
        Native git @('clone', '--quiet', $AppRepo, $app)
        Native git @('-C', $app, 'remote', 'set-url', '--push', 'origin', 'DISABLED')
        Write-Host '(나) pack'
        Native $tedit @('pack', $app, $Site)
        $diff = 0
        foreach ($f in Get-ChildItem (Join-Path $AppRepo "site\$Site") -File -Filter *_packed.c) {
            $a = (Get-FileHash $f.FullName).Hash; $b = (Get-FileHash (Join-Path $app "site\$Site\$($f.Name)")).Hash
            Write-Host ("  {0} {1}" -f $f.Name, $(if ($a -eq $b) { '바이트 동일' } else { '다름' }))
            if ($a -ne $b) { $diff++ }
        }
        if ($diff) { throw '생성물이 저장소 것과 다름' }
        $st = git -C $app status --porcelain
        if ($st) { throw "git status 깨끗하지 않음:`n$st" }
        Write-Host '  git status 깨끗'
        Write-Host '(다) build'
        Native $tedit @('build', $app, $Site)
    } else {
        Write-Host '(나)(다) 건너뜀: -AppRepo 없음'
    }

    Write-Host '--shot'
    $png = Join-Path $tmp 'shot.png'
    $p = Start-Process (Join-Path $pack 'TableEditor.exe') -ArgumentList @('--site', "`"$demo`"", '--shot', "`"$png`"") -PassThru
    if (-not $p.WaitForExit(90000)) { $p.Kill(); throw '--shot 시간 초과' }
    if ($p.ExitCode -ne 0) { throw "--shot 종료 코드 $($p.ExitCode)" }
    if (-not (Test-Path $png) -or (Get-Item $png).Length -lt 1000) { throw '--shot PNG 없음' }
    $sig = [IO.File]::ReadAllBytes($png)[1..3] -join ','
    if ($sig -ne '80,78,71') { throw '--shot 결과가 PNG 아님' }
    Write-Host "  PNG $((Get-Item $png).Length) 바이트, 종료 코드 0"
}
finally {
    $env:PATH = $origPath
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Step 6 'VERSION·zip'
[IO.File]::WriteAllText((Join-Path $pack 'VERSION'), "$Version`r`n", (New-Object Text.UTF8Encoding $false))
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($pack, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
[IO.File]::WriteAllText("$zip.sha256", "$hash  $(Split-Path -Leaf $zip)`n", (New-Object Text.UTF8Encoding $false))
Write-Host "묶음   $(Size $pack)  ($pack)"
Write-Host "gcc    $(Size $gdst)"
Write-Host ("zip    {0:N1} MB  ({1})" -f ((Get-Item $zip).Length / 1MB), $zip)
Write-Host "sha256 $hash"
Write-Host ("걸린 시간 {0:N0}초" -f $sw.Elapsed.TotalSeconds)
