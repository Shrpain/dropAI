# Sync PC to GitHub
Write-Host "🚀 Đang đẩy bản vá lỗi bộ nhớ lên GitHub..." -ForegroundColor Cyan

# 1. Add all changes
git add .

# 2. Commit
$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
git commit -m "Fix memory crash (malloc) on phone: $timestamp"

# 3. Push
git push origin main

if ($LASTEXITCODE -eq 0) {
    Write-Host "✅ Đã đẩy bản vá lên GitHub!" -ForegroundColor Green
}
