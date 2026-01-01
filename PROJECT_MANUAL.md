# 📄 HƯỚNG DẪN VẬN HÀNH BOT DROPAI (FULL)

Tài liệu này mô tả chi tiết cách thức hoạt động của hệ thống Bot tự động cược và cách đồng bộ code từ máy tính lên điện thoại.

---

## 🏗 1. KIẾN TRÚC HỆ THỐNG
Bot được viết bằng **C# (.NET 8)** với các thành phần chính:
- **GameApiService**: Trái tim của hệ thống. Quản lý việc đăng nhập, lấy lịch sử game, tính toán số dư và đặt cược.
- **AiStrategyService**: Bộ não AI. Phân tích lịch sử dựa trên nhiều thuật toán (Markov, Bayesian, Pattern Matcher...) để đưa ra dự đoán.
- **TelegramBotService**: Giao diện điều khiển. Cho phép người dùng ra lệnh qua Telegram.

### Cơ chế đặt cược:
1. **Polling**: Bot liên tục kiểm tra kết quả game 1 giây/lần.
2. **Result Detection**: Khi thấy kết quả mới, Bot dừng cược phiên cũ ngay lập tức.
3. **Smart Re-Sync**: Nếu phiên bị "Settled" (404), Bot tự động hỏi máy chủ phiên tiếp theo là gì và nhảy phiên.
4. **Aggressive Retry**: Nếu đặt cược lỗi, Bot sẽ spam liên tục (5 lần, cách nhau 300ms) để đảm bảo không bị hụt phiên.

---

## 🤖 2. CHI TIẾT CÁC CHIẾN THUẬT AI
Bot sử dụng hệ thống **Ensemble (Đồng thuận)** kết hợp nhiều chiến thuật:
- **Markov Order 4**: Phân tích xác suất dựa trên chuỗi 4 kết quả gần nhất.
- **Bayesian**: Phân tích tần suất xuất hiện của kết quả trong lịch sử dài hạn.
- **Pattern Matcher**: Tìm kiếm các mẫu hình (Cầu 1-1, Cầu bệt, Cầu 2-2...) trong quá khứ.
- **Streak Follower**: Bắt cầu bệt khi thấy một bên thắng liên tiếp.
- **ZigZag**: Dự đoán sự thay đổi khi kết quả dao động liên tục.

---

## 📱 3. HƯỚNG DẪN HOST TRÊN ĐIỆN THOẠI (TERMUX)
Đây là cách treo Bot 24/7 mà không cần máy tính.

### A. Cài đặt môi trường (Làm 1 lần duy nhất)
1. Tải ứng dụng **Termux** từ F-Droid.
2. Gõ lệnh cài Ubuntu:
   ```bash
   pkg install proot-distro && proot-distro install ubuntu
   ```
3. Đăng nhập vào Ubuntu: `proot-distro login ubuntu`
4. Cài đặt .NET 8 (Script tự động):
   ```bash
   apt update && apt install -y wget git libicu-dev openssl
   wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
   chmod +x ./dotnet-install.sh
   ./dotnet-install.sh --channel 8.0
   echo 'export DOTNET_ROOT=$HOME/.dotnet' >> ~/.bashrc
   echo 'export PATH=$PATH:$HOME/.dotnet' >> ~/.bashrc
   echo 'export DOTNET_GCHeapHardLimit=1C000000' >> ~/.bashrc
   source ~/.bashrc
   ```

### B. Tải Bot về lần đầu
```bash
git clone https://github.com/Shrpain/dropAI.git dropbot
cd dropbot
chmod +x sync_phone.sh
./sync_phone.sh
```

---

## 🔄 4. QUY TRÌNH CẬP NHẬT CODE 1-CLICK
Khi bạn sửa code AI trên máy tính và muốn điện thoại cập nhật theo:

### Bước 1: Trên MÁY TÍNH
1. Sửa code xong.
2. Click chuột phải vào file **`sync_pc.ps1`** chọn **Run with PowerShell**.
3. File này sẽ tự động đẩy code lên GitHub.

### Bước 2: Trên ĐIỆN THOẠI
1. Mở Termux, vào Ubuntu: `proot-distro login ubuntu`
2. Vào thư mục bot: `cd ~/dropbot`
3. Gõ lệnh: **`./sync_phone.sh`**
4. Xong! Bot sẽ tự lấy code mới, tự biên dịch và khởi động lại.

---

## ⌨️ 5. CÁC LỆNH ĐIỀU KHIỂN TELEGRAM
- `📊 Trạng thái`: Xem số dư, cấu hình Martingale hiện tại và kết quả 10 ván gần nhất.
- `▶ Bật Auto`: Bắt đầu tiến trình tự động cược theo AI.
- `⏸ Tắt Auto`: Dừng cược ngay lập tức.
- `⚙ Cấu hình Martingale`: Nhập dãy số cược (VD: 2,4,8,19,40,90).
- `💰 Cấu hình Vốn`: Nhập số tiền cược gốc (VD: 1000).

---

**⚠️ Lưu ý vận hành**:
- Luôn giữ Termux chạy ngầm bằng cách nhấn **Acquire wakelock** trên thanh thông báo.
- Cắm sạc liên tục để CPU điện thoại không bị hạ xung, giúp Bot bắt cầu nhanh nhất.
