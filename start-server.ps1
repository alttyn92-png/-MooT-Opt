Set-Location -LiteralPath $PSScriptRoot
if (-not $env:ALEM_ENDPOINT) { $env:ALEM_ENDPOINT = 'https://llm.alem.ai/v1/chat/completions' }
if (-not $env:ALEM_MODEL) { $env:ALEM_MODEL = 'qwen3-8' }
if (-not $env:ALEM_API_KEY) {
    $mootSecret = Read-Host 'API key Alem Plus (hidden input)' -AsSecureString
    $mootSecretPtr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($mootSecret)
    try { $env:ALEM_API_KEY = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($mootSecretPtr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($mootSecretPtr) }
}
python serve.py
