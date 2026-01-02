#!/bin/bash

# Sync Phone from GitHub and Restart Bot
# 1. Setup paths and memory limits for ARM64/Termux
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$PATH:$HOME/.dotnet
export DOTNET_gcServer=0
export DOTNET_GCHeapHardLimit=1C000000
export DOTNET_TieredCompilation=0
export DOTNET_ReadyToRun=0
export DOTNET_EnableWriteXorExecute=0
export COMPlus_EnableDiagnostics=0

# Chạy trực tiếp từ file thực thi thay vì dotnet run
./out/DropAI
