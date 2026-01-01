#!/bin/bash

# Sync Phone from GitHub and Restart Bot
echo "🚀 Đang lấy code mới từ GitHub..."

# 1. Pull latest
git pull origin main

# 2. Build for local architecture (Already have .NET 8 on phone)
echo "🔨 Đang biên dịch Bot..."
dotnet build -c Release

# 3. Run Bot in background (optional) or just run
echo "✅ Hoàn tất! Chạy Bot..."
dotnet run -c Release --project .
