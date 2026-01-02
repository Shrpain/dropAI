#!/bin/bash

# 1. Setup stability paths and memory limits for ARM64/Termux
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
sleep 1

# 3. Pull latest pre-compiled files from GitHub
echo "🚀 Đang tải bản biên dịch sẵn (ARM64) từ GitHub..."
git reset --hard
git pull origin main

# 4. Check if build exists and Run (Use phone_build instead of out)
if [ -f "./phone_build/DropAI" ]; then
    echo "✅ Đã tìm thấy bản build ARM64. Khởi động Bot..."
    chmod +x ./phone_build/DropAI
    ./phone_build/DropAI
else
    echo "❌ Lỗi: Không tìm thấy file thực thi trong phone_build/. Vui lòng kiểm tra lại GitHub."
    exit 1
fi
