#!/usr/bin/env pwsh
param(
	# Which version segment to increment. AssemblyVersion is only changed for a
	# "major" bump; "minor" and "patch" leave AssemblyVersion untouched (so strong-name
	# binding stays stable) while still bumping FileVersion and Version.
	[ValidateSet("major", "minor", "patch")]
	[string]$BumpType = "patch"
)
$ErrorActionPreference = "Stop"
$CURRENTPATH=$pwd.Path

$SegmentMap = @{ major = 1; minor = 2; patch = 3 }
$Segment = $SegmentMap[$BumpType]


function RunVerionUpdater($loc, $path){
	If ($IsWindows) {
	    & "./tools/AssemblyInfoUtil.exe" -inc:$loc "$path"
	}
	else {
		assemblyinfoutil -inc:$loc "$path"
	}
}

function Get-AssemblyVersion($path){
	[xml]$props = Get-Content $path
	return ($props.Project.PropertyGroup.AssemblyVersion | Where-Object { $_ }) | Select-Object -First 1
}

function Set-AssemblyVersion($path, $value){
	[xml]$props = Get-Content $path
	foreach ($pg in $props.Project.PropertyGroup) {
		if ($pg.AssemblyVersion) { $pg.AssemblyVersion = $value }
	}
	$props.Save($path)
}

function package_parameterproperties() {
	Write-Output "package_parameterproperties"

	$GITHASH = git rev-parse --short HEAD
	[xml]$props = Get-Content "$CURRENTPATH/Directory.Build.props"
	$VERSION = ($props.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
	if ([string]::IsNullOrWhiteSpace($VERSION)) { throw "Could not read Version from Directory.Build.props" }

	if(!(Test-Path "$CURRENTPATH/build")){
		mkdir "$CURRENTPATH/build"
	}
	if (Test-Path "$CURRENTPATH/build/parameterproperties.txt"){
		Remove-Item -force "$CURRENTPATH/build/parameterproperties.txt"
	}
	Add-Content "$CURRENTPATH/build/parameterproperties.txt" "VERSION=$VERSION"
	Add-Content "$CURRENTPATH/build/parameterproperties.txt" "GITHASH=$GITHASH"
	Add-Content "$CURRENTPATH/build/parameterproperties.txt" "GITHASH_FULL=$(git rev-parse HEAD)"

	Set-Location "$CURRENTPATH"
	$pwd.Path
}

$propsPath = "$CURRENTPATH/Directory.Build.props"

# Snapshot AssemblyVersion so we can restore it after the bump for non-major releases.
$originalAssemblyVersion = Get-AssemblyVersion $propsPath

RunVerionUpdater $Segment $propsPath

if ($BumpType -ne "major") {
	Set-AssemblyVersion $propsPath $originalAssemblyVersion
}

package_parameterproperties
