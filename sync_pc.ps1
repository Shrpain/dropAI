# Sync PC to GitHub
Write-Host "🚀 Đang chuẩn bị đẩy code lên GitHub..." -ForegroundColor Cyan

# 1. Add all changes
git add .

# 2. Commit with timestamp
$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
git commit -m "Update from PC: $timestamp"

# 3. Push to GitHub
git push origin main

if ($LASTEXITCODE -eq 0) {
    Write-Host "✅ Đã đẩy code lên GitHub thành công!" -ForegroundColor Green
} else {
    Write-Host "❌ Có lỗi xảy ra khi đẩy code." -ForegroundColor Red
}
