#!/bin/bash

# 1. Setup paths and memory limits for ARM64/Termux Stability
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$PATH:$HOME/.dotnet
export DOTNET_gcServer=0
export DOTNET_GCHeapHardLimit=10000000
export DOTNET_TieredCompilation=1
export DOTNET_ReadyToRun=0
export DOTNET_EnableWriteXorExecute=0
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
export COMPlus_EnableDiagnostics=0

# 2. Cleanup existing processes to free RAM
echo "🧹 Đang dọn dẹp bộ nhớ..."
pkill -f DropAI || true

# 3. Pull latest code
echo "🚀 Đang lấy code mới từ GitHub..."
git reset --hard
git pull origin main

# 4. Build/Publish (Conservative mode to save RAM)
echo "🔨 Đang biên dịch (Chế độ tiết kiệm RAM)..."
rm -rf ./out
dotnet publish -c Release -o ./out -p:Parallel=false -p:TieredCompilation=false

# 5. Check if build success
if [ -f "./out/DropAI" ]; then
    echo "✅ Hoàn tất! Khởi động Bot..."
    chmod +x ./out/DropAI
    ./out/DropAI
else
    echo "❌ Lỗi: Không tìm thấy file thực thi sau khi build."
    exit 1
fi
