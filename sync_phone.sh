#!/bin/bash

# 1. Setup paths and memory limits for ARM64/Termux
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$PATH:$HOME/.dotnet
export DOTNET_gcServer=0
export DOTNET_GCHeapHardLimit=1C000000
export DOTNET_TieredCompilation=0
export DOTNET_ReadyToRun=0
export DOTNET_EnableWriteXorExecute=0

# 2. Pull latest code
echo "🚀 Đang lấy code mới từ GitHub..."
git reset --hard
git pull origin main

# 3. Build/Publish (Stable)
echo "🔨 Đang biên dịch bản ổn định (Publish)..."
rm -rf ./out
dotnet publish -c Release -o ./out

# 4. Check if build success
if [ -f "./out/DropAI" ]; then
    echo "✅ Hoàn tất! Khởi động Bot..."
    chmod +x ./out/DropAI
    ./out/DropAI
else
    echo "❌ Lỗi: Không tìm thấy file thực thi sau khi build."
    exit 1
fi
