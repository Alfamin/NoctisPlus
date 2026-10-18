# Uses a new empty profile; never imports a real profile or copies a Telegram session.
& (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'Start-NoctisPlus-Test.ps1') -FreshProfile
exit $LASTEXITCODE
