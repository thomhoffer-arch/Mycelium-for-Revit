#Requires -Version 5.1
<#
.SYNOPSIS
  Conformance self-test for the Revit connector's live MCP endpoint.

.DESCRIPTION
  Calls every tool against a model open in a running Revit instance and checks the
  response shape against docs/CONTRACT.md, so field-name drift (a tool documented as
  returning a field it doesn't, or vice versa) fails loudly instead of being caught
  only when Loam's connector-agnostic layer hits it live.

  This is NOT a unit test and cannot run in CI: it needs Revit open with a model
  loaded and the connector's MCP server listening (see README.md). Run it manually
  after a change to any Pdra/Tools/*.cs file, or on a schedule against a reference
  model.

  Focus: the classification work (ROADMAP.md "Fixed" — classification reachability).
  In particular this proves Loam's ask directly: that list_elements (bulk) and
  get_element_by_uniqueid / get_element_by_ifcguid (by-ID) return the SAME
  classification for the SAME element — not just that each individually returns
  something. Also covers the second round of that feedback: get_classification_sources'
  scope="all" must surface instance-level candidates name-agnostically (not just
  type-level, string-only, name-matched ones), legacy all=true must keep behaving
  exactly like scope="type", and a row's category_id must round-trip as another
  tool's category argument.

  Environment overrides (optional, same as install.ps1):
    $env:MYCELIUM_REVIT_URL     MCP server URL   (default http://127.0.0.1:47100/mcp)
    $env:MYCELIUM_REVIT_TOKEN   Bearer token      (default: none)
    $env:MYCELIUM_SELFTEST_CATEGORY  BuiltInCategory to sample for the classification
                                      checks (default: OST_Walls)

.EXAMPLE
  pwsh ./tools/selftest.ps1
#>

$ErrorActionPreference = 'Stop'

$Url      = if ($env:MYCELIUM_REVIT_URL)   { $env:MYCELIUM_REVIT_URL }   else { 'http://127.0.0.1:47100/mcp' }
$Token    = $env:MYCELIUM_REVIT_TOKEN
$Category = if ($env:MYCELIUM_SELFTEST_CATEGORY) { $env:MYCELIUM_SELFTEST_CATEGORY } else { 'OST_Walls' }

$script:FailCount = 0
$script:PassCount = 0
$script:NextId    = 1

function Write-Pass([string]$Message) {
    $script:PassCount++
    Write-Host "  [PASS] $Message" -ForegroundColor Green
}

function Write-Fail([string]$Message) {
    $script:FailCount++
    Write-Host "  [FAIL] $Message" -ForegroundColor Red
}

# Calls one MCP tool via JSON-RPC tools/call and returns the parsed response object
# (the tool's own JSON, already unwrapped from result.content[0].text).
function Invoke-PdraTool {
    param(
        [Parameter(Mandatory)][string]$Name,
        [hashtable]$Arguments = @{}
    )

    $id = $script:NextId++
    $body = @{
        jsonrpc = '2.0'
        id      = $id
        method  = 'tools/call'
        params  = @{ name = $Name; arguments = $Arguments }
    } | ConvertTo-Json -Depth 10

    $headers = @{ 'Content-Type' = 'application/json' }
    if ($Token) { $headers['Authorization'] = "Bearer $Token" }

    $resp = Invoke-RestMethod -Uri $Url -Method Post -Body $body -Headers $headers

    if ($resp.error) {
        throw "tools/call '$Name' returned a JSON-RPC error: $($resp.error.message)"
    }

    $text = $resp.result.content[0].text
    if (-not $text) { throw "tools/call '$Name' returned no content[0].text." }

    $parsed = $text | ConvertFrom-Json
    if ($resp.result.isError) {
        throw "Tool '$Name' reported isError: $($parsed | ConvertTo-Json -Compress)"
    }
    return $parsed
}

# True when $Object has a property named $Name (works for both PSCustomObject and hashtable-ish results).
function Test-HasProperty {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $false }
    return [bool]($Object.PSObject.Properties.Name -contains $Name)
}

Write-Host ""
Write-Host "==> Revit connector self-test against $Url" -ForegroundColor Cyan
Write-Host "    Sampling category: $Category"
Write-Host ""

# ── 1) Field-shape checks against docs/CONTRACT.md ─────────────────────────────

Write-Host "-- Shape checks --" -ForegroundColor Cyan

$rev = Invoke-PdraTool -Name 'get_model_revision'
foreach ($f in @('version_guid', 'number_of_saves', 'has_unsaved_changes', 'title', 'path')) {
    if (Test-HasProperty $rev $f) { Write-Pass "get_model_revision has '$f'" }
    else { Write-Fail "get_model_revision is missing '$f' (docs/CONTRACT.md)" }
}

# worksharing/central-model identity fields (docs/CONTRACT.md "Cross-user model identity") — the
# fields that let two users' events about the SAME shared model be recognised as such, since
# title/path above are each user's own local copy and differ per user.
$allowedWorksharing = @('cloud', 'not_workshared', 'file_based_central', 'file_based_local', 'file_based_unknown')
if (-not (Test-HasProperty $rev 'worksharing')) {
    Write-Fail "get_model_revision is missing 'worksharing' (docs/CONTRACT.md)"
} elseif ($allowedWorksharing -notcontains $rev.worksharing) {
    Write-Fail "get_model_revision.worksharing = '$($rev.worksharing)' is not one of: $($allowedWorksharing -join ', ')"
} else {
    Write-Pass "get_model_revision.worksharing = '$($rev.worksharing)'"

    if ($rev.worksharing -in @('file_based_local', 'file_based_central')) {
        if (Test-HasProperty $rev 'central_model_path') { Write-Pass "get_model_revision has 'central_model_path' for worksharing='$($rev.worksharing)'" }
        else { Write-Fail "get_model_revision is missing 'central_model_path' for worksharing='$($rev.worksharing)'" }
    } elseif ($rev.worksharing -eq 'cloud') {
        foreach ($f in @('cloud_project_guid', 'cloud_model_guid', 'cloud_region')) {
            if (Test-HasProperty $rev $f) { Write-Pass "get_model_revision has '$f' for worksharing='cloud'" }
            else { Write-Fail "get_model_revision is missing '$f' for worksharing='cloud'" }
        }
    }
}

$list = Invoke-PdraTool -Name 'list_elements' -Arguments @{ category = $Category; limit = 50 }
foreach ($f in @('count', 'truncated', 'elements', 'classification_sources')) {
    if (Test-HasProperty $list $f) { Write-Pass "list_elements has '$f'" }
    else { Write-Fail "list_elements is missing '$f' (docs/CONTRACT.md)" }
}
if (-not (Test-HasProperty $list.classification_sources 'probed')) {
    Write-Fail "list_elements.classification_sources is missing 'probed'"
} else {
    Write-Pass "list_elements.classification_sources.probed present ($($list.classification_sources.probed.Count) entries)"
}

if ($list.elements.Count -eq 0) {
    Write-Fail "list_elements returned 0 elements for category '$Category' — pick a category present in the open model via `$env:MYCELIUM_SELFTEST_CATEGORY."
} else {
    Write-Pass "list_elements returned $($list.elements.Count) elements"
    foreach ($f in @('unique_id', 'id', 'category', 'name')) {
        if (Test-HasProperty $list.elements[0] $f) { Write-Pass "list_elements row has '$f'" }
        else { Write-Fail "list_elements row is missing '$f'" }
    }
}

# ── 2) Loam's ask: bulk (list_elements) vs by-ID (get_element_by_uniqueid /
#      get_element_by_ifcguid) must return the SAME classification for the SAME
#      element. Requires at least one classified element in the sampled category —
#      if none, this reports a skip (not a pass), since there's nothing to compare.

Write-Host ""
Write-Host "-- Bulk vs by-ID classification equality (Loam's conformance ask) --" -ForegroundColor Cyan

$classifiedRow = $list.elements | Where-Object { Test-HasProperty $_ 'classification' } | Select-Object -First 1

if (-not $classifiedRow) {
    Write-Host "  [SKIP] No classified element found in the first $($list.elements.Count) of category '$Category'." -ForegroundColor Yellow
    Write-Host "         Re-run with -classification_params pointing at the model's real classification" -ForegroundColor Yellow
    Write-Host "         parameter (see get_classification_sources), or a category known to carry Assembly Code." -ForegroundColor Yellow
} else {
    $uid = $classifiedRow.unique_id
    Write-Host "  Comparing unique_id $uid ..."

    $byUid = Invoke-PdraTool -Name 'get_element_by_uniqueid' -Arguments @{ unique_id = $uid }
    $byUidRow = $byUid.elements[0]

    if (-not (Test-HasProperty $byUidRow 'classification')) {
        Write-Fail "get_element_by_uniqueid($uid) has no classification, but list_elements returned one for the same element."
    } else {
        $bulkJson = $classifiedRow.classification | ConvertTo-Json -Compress -Depth 10
        $uidJson  = $byUidRow.classification      | ConvertTo-Json -Compress -Depth 10
        if ($bulkJson -eq $uidJson) {
            Write-Pass "list_elements and get_element_by_uniqueid agree: $bulkJson"
        } else {
            Write-Fail "classification MISMATCH for $uid — list_elements: $bulkJson vs get_element_by_uniqueid: $uidJson"
        }
    }

    if (Test-HasProperty $classifiedRow 'ifc_guid') {
        $guid = $classifiedRow.ifc_guid
        $byGuid = Invoke-PdraTool -Name 'get_element_by_ifcguid' -Arguments @{ ifc_guid = $guid }
        $byGuidRow = $byGuid.elements[0]
        if (-not (Test-HasProperty $byGuidRow 'classification')) {
            Write-Fail "get_element_by_ifcguid($guid) has no classification, but list_elements returned one for the same element."
        } else {
            $bulkJson  = $classifiedRow.classification | ConvertTo-Json -Compress -Depth 10
            $guidJson  = $byGuidRow.classification      | ConvertTo-Json -Compress -Depth 10
            if ($bulkJson -eq $guidJson) {
                Write-Pass "list_elements and get_element_by_ifcguid agree: $bulkJson"
            } else {
                Write-Fail "classification MISMATCH for ifc_guid $guid — list_elements: $bulkJson vs get_element_by_ifcguid: $guidJson"
            }
        }
    } else {
        Write-Host "  [SKIP] Sampled element has no ifc_guid — can't cross-check get_element_by_ifcguid." -ForegroundColor Yellow
    }
}

# ── 3) classification_sources.probed labels stay identical across tools for the
#      same classification_params — a caller must be able to trust the envelope
#      shape regardless of which tool it called.

Write-Host ""
Write-Host "-- classification_sources consistency across tools --" -ForegroundColor Cyan

if (-not $classifiedRow) {
    Write-Host "  [SKIP] No classified element from step 2 to probe with." -ForegroundColor Yellow
} else {
    $clsParams = @('NL-SfB')
    $fromList = (Invoke-PdraTool -Name 'list_elements' -Arguments @{ category = $Category; limit = 5; classification_params = $clsParams }).classification_sources.probed | ForEach-Object { $_.label }
    $fromUid  = (Invoke-PdraTool -Name 'get_element_by_uniqueid' -Arguments @{ unique_id = $classifiedRow.unique_id; classification_params = $clsParams }).classification_sources.probed | ForEach-Object { $_.label }

    if (($fromList -join ',') -eq ($fromUid -join ',')) {
        Write-Pass "classification_sources.probed labels match between list_elements and get_element_by_uniqueid: [$($fromList -join ', ')]"
    } else {
        Write-Fail "classification_sources.probed labels differ — list_elements: [$($fromList -join ', ')] vs get_element_by_uniqueid: [$($fromUid -join ', ')]"
    }
}

# ── 4) filter_elements_by_scope_box no longer blanks level/design_option/
#      classification — it used to assign them unconditionally, which serialized
#      as an explicit "level": null instead of omitting the key. ConvertFrom-Json
#      distinguishes the two: a present-but-null property still shows up in
#      PSObject.Properties.Name, an omitted one doesn't — so Test-HasProperty
#      returning $true with a $null value below IS the regression.

Write-Host ""
Write-Host "-- filter_elements_by_scope_box omit-never-blank regression check --" -ForegroundColor Cyan

$boxes = Invoke-PdraTool -Name 'list_elements' -Arguments @{ category = 'OST_VolumeOfInterest'; limit = 1 }
if ($boxes.elements.Count -eq 0) {
    Write-Host "  [SKIP] No scope box (OST_VolumeOfInterest) in the open model." -ForegroundColor Yellow
} else {
    $boxId = $boxes.elements[0].id
    $filtered = Invoke-PdraTool -Name 'filter_elements_by_scope_box' -Arguments @{ scope_box_id = $boxId; category = $Category; limit = 50 }

    foreach ($f in @('scope_box', 'mode', 'count_in', 'count_out', 'elements', 'classification_sources')) {
        if (Test-HasProperty $filtered $f) { Write-Pass "filter_elements_by_scope_box has '$f'" }
        else { Write-Fail "filter_elements_by_scope_box is missing '$f' (docs/CONTRACT.md)" }
    }

    $blanked = @()
    foreach ($row in $filtered.elements) {
        foreach ($f in @('level', 'design_option', 'classification')) {
            if ((Test-HasProperty $row $f) -and ($null -eq $row.$f)) { $blanked += $f }
        }
    }
    if ($blanked.Count -eq 0) {
        Write-Pass "filter_elements_by_scope_box never blanks level/design_option/classification ($($filtered.elements.Count) rows checked)"
    } else {
        Write-Fail "filter_elements_by_scope_box blanked these fields instead of omitting them: $($blanked | Select-Object -Unique -join ', ')"
    }
}

# ── 5) get_classification_sources scope: "all" is instance-capable and name-agnostic —
#      the actual gap the second round of Loam/NLRS feedback reported: with the legacy
#      all=true / scope="type", an instance-level parameter with a non-classification
#      -looking name is invisible no matter what argument is passed. scope="all" (both
#      levels, no name/storage filter) must surface it, unscoped, spread across the
#      document's categories rather than the first `sample` in document order.

Write-Host ""
Write-Host "-- get_classification_sources scope --" -ForegroundColor Cyan

$sourcesAll = Invoke-PdraTool -Name 'get_classification_sources' -Arguments @{ scope = 'all'; sample = 300 }
foreach ($f in @('sampled', 'sources')) {
    if (Test-HasProperty $sourcesAll $f) { Write-Pass "get_classification_sources(scope=all) has '$f'" }
    else { Write-Fail "get_classification_sources(scope=all) is missing '$f' (docs/CONTRACT.md)" }
}

$instanceCandidates = $sourcesAll.sources | Where-Object { $_.level -eq 'instance' }
if ($instanceCandidates.Count -eq 0) {
    Write-Host "  [SKIP] scope=all returned no instance-level candidates — either the open model has none," -ForegroundColor Yellow
    Write-Host "         or the 300-element sample missed them; re-run with a larger 'sample'." -ForegroundColor Yellow
} else {
    Write-Pass "get_classification_sources(scope=all) reports $($instanceCandidates.Count) instance-level candidate(s), e.g. '$($instanceCandidates[0].parameter)' (populated $($instanceCandidates[0].populated)/$($sourcesAll.sampled))"
}

# Legacy all=true must still behave exactly like scope="type" (type-level, String-only,
# no name filter) — the explicit backward-compatibility requirement.
$legacyAll = Invoke-PdraTool -Name 'get_classification_sources' -Arguments @{ all = $true; category = $Category; sample = 100 }
$scopeType = Invoke-PdraTool -Name 'get_classification_sources' -Arguments @{ scope = 'type'; category = $Category; sample = 100 }
$legacyNames = ($legacyAll.sources | Sort-Object level, parameter | ForEach-Object { "$($_.level):$($_.parameter)" }) -join ','
$typeNames   = ($scopeType.sources | Sort-Object level, parameter | ForEach-Object { "$($_.level):$($_.parameter)" }) -join ','
if ($legacyNames -eq $typeNames) {
    Write-Pass "legacy all=true matches scope='type' ($($legacyAll.sources.Count) candidates) — no behavior change for existing callers"
} else {
    Write-Fail "legacy all=true diverges from scope='type' — all=true: [$legacyNames] vs scope=type: [$typeNames]"
}

# ── 6) Category round-trip: category_id read off a list_elements row must resolve
#      when fed back as another tool's `category` argument — previously always failed
#      with "Unknown BuiltInCategory" because list_elements emitted the display name
#      ("Walls") while every tool's `category` arg only parsed the enum name.

Write-Host ""
Write-Host "-- category round-trip --" -ForegroundColor Cyan

if ($list.elements.Count -eq 0 -or -not (Test-HasProperty $list.elements[0] 'category_id')) {
    Write-Host "  [SKIP] No list_elements row with 'category_id' to round-trip (category '$Category' may not be a built-in category)." -ForegroundColor Yellow
} else {
    $roundTripId = $list.elements[0].category_id
    Write-Pass "list_elements row carries category_id '$roundTripId' alongside category '$($list.elements[0].category)'"

    $byEnumName = Invoke-PdraTool -Name 'list_elements' -Arguments @{ category = $roundTripId; limit = 1 }
    if ($byEnumName.elements.Count -gt 0) {
        Write-Pass "category_id '$roundTripId' round-trips as a category arg (enum-name path)"
    } else {
        Write-Fail "category_id '$roundTripId' fed back as category returned 0 elements"
    }

    $byDisplayName = Invoke-PdraTool -Name 'list_elements' -Arguments @{ category = $list.elements[0].category; limit = 1 }
    if ($byDisplayName.elements.Count -gt 0) {
        Write-Pass "display name '$($list.elements[0].category)' also round-trips as a category arg (display-name fallback)"
    } else {
        Write-Fail "display name '$($list.elements[0].category)' fed back as category returned 0 elements"
    }
}

Write-Host ""
Write-Host "==> $script:PassCount passed, $script:FailCount failed" -ForegroundColor $(if ($script:FailCount -eq 0) { 'Green' } else { 'Red' })
if ($script:FailCount -gt 0) { exit 1 }
exit 0
