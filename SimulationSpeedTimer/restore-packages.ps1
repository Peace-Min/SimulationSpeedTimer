# NuGet 패키지 복원 스크립트
# Visual Studio에서 실행하거나, 패키지 관리자 콘솔에서 실행하세요

Write-Host "System.Data.SQLite.Core 패키지 복원 중..." -ForegroundColor Yellow

# NuGet.exe가 있는지 확인
$nugetPath = "nuget.exe"
if (-not (Test-Path $nugetPath)) {
    Write-Host "NuGet.exe 다운로드 중..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe" -OutFile "nuget.exe"
}

# 패키지 복원
& $nugetPath restore packages.config -PackagesDirectory ..\packages

Write-Host "패키지 복원 완료!" -ForegroundColor Green
