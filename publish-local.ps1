param(
	[string]$Output = "C:\Users\Marc\.local-nuget",
	[string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$src = Join-Path $PSScriptRoot "src"

New-Item -ItemType Directory -Force $Output | Out-Null

$projects = @(
	"Jacobi.BinarySerializer\Jacobi.BinarySerializer.csproj",
	"Jacobi.BinarySerializer.Processors\Jacobi.BinarySerializer.Processors.csproj"
)

foreach ($project in $projects) {
	dotnet pack (Join-Path $src $project) -c $Configuration -o $Output
	if ($LASTEXITCODE -ne 0) { throw "Pack failed for $project" }
}

Get-ChildItem $Output -Filter "Jacobi.BinarySerializer*.nupkg"
