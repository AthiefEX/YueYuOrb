param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference='Stop'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($Path,[ref]$tokens,[ref]$errors)
if ($errors.Count -gt 0) { throw 'Guest administration script has syntax errors.' }
$commands=$ast.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Add-Type'},$true)
if ($commands.Count -ne 2) { throw 'Expected fixed WTS and LSA helper definitions.' }
foreach ($command in $commands) {
    $literal=$command.CommandElements[2]
    if ($literal -isnot [Management.Automation.Language.StringConstantExpressionAst]) { throw 'Expected fixed helper source.' }
    Add-Type -TypeDefinition $literal.Value
}
Write-Output 'PowerShell script parsed; WTS and LSA helpers compiled; no guest action or LSA operation executed.'
