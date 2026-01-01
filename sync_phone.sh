#!/bin/bash

# Sync Phone from GitHub and Restart Bot
echo "🚀 Đang lấy code mới từ GitHub..."

# 1. Pull latest
git pull origin main

# 2. Build/Publish for local architecture
echo "🔨 Đang biên dịch bản ổn định (Publish)..."
dotnet publish -c Release -o ./out

# 3. Run Bot directly from binary (Stable)
echo "✅ Hoàn tất! Khởi động Bot..."
export DOTNET_gcServer=0
export DOTNET_GCHeapHardLimit=1C000000
export DOTNET_TieredCompilation=0
export DOTNET_ReadyToRun=0
export DOTNET_EnableWriteXorExecute=0
export COMPlus_EnableDiagnostics=0

# Chạy trực tiếp từ file thực thi thay vì dotnet run
./out/DropAI
