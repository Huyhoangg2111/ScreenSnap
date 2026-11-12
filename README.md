# ScreenSnap — Chụp màn hình chọn vùng cho Windows

Một file `dist\ScreenSnap.exe` duy nhất (~90 KB), không cần cài đặt, chạy trên Windows 10/11 (.NET Framework 4 có sẵn).

## Bắt đầu
1. Copy `dist\ScreenSnap.exe` tới nơi cố định (vd `C:\Tools\ScreenSnap\`) rồi chạy.
2. Lần đầu sẽ mở **Cài đặt**: chọn phím tắt, thư mục lưu, bật *Chạy cùng Windows* / *Quyền Administrator* nếu muốn.
3. App nằm ở khay hệ thống (góc phải taskbar). Click trái vào biểu tượng = chụp; click phải = menu.

> Nếu bật "Chạy cùng Windows" rồi mới di chuyển file exe, hãy mở lại app một lần ở vị trí mới để cập nhật đường dẫn.

## Phím tắt mặc định
| Phím | Chức năng |
|---|---|
| `PrintScreen` | Chụp vùng chọn (có vẽ / chú thích) |
| `Ctrl + PrintScreen` | Chụp toàn màn hình, lưu & copy ngay |

Nếu phím bị phần mềm khác chiếm (vd Zalo đang giữ `Ctrl+Alt+A`), app sẽ báo và bạn đổi trong Cài đặt.
Có thể tạo shortcut tới `ScreenSnap.exe --capture` (ghim taskbar / gán phím trong Properties) để mở màn hình chụp.

## Trong màn hình chụp
- Kéo chuột để chọn vùng; rê lên một cửa sổ rồi click để chọn nguyên cửa sổ; `Ctrl+A` = cả màn hình.
- Kéo ô vuông ở viền để chỉnh kích thước, kéo bên trong để di chuyển; phím mũi tên dịch 1px (Shift = đổi cỡ).
- Công cụ: `R` chữ nhật · `E` elip · `A` mũi tên · `L` đường thẳng · `P` bút · `H` dạ quang · `T` chữ · `N` đánh số · `M` làm mờ. Giữ `Shift` để vẽ vuông/tròn/thẳng 45°.
- Chọn 8 màu và 3 cỡ nét ở hàng thứ 2 của thanh công cụ.
- `Enter` / `Ctrl+C` / double-click: copy vào clipboard · `Ctrl+S`: lưu nhanh (kèm copy) · `Ctrl+Shift+S`: lưu thành... · `Ctrl+Z`/`Ctrl+Y`: hoàn tác/làm lại · chuột phải: chọn lại · `Esc`: thoát.

## Quyền Administrator & khởi động cùng Windows
- Quyền thường: dùng khóa `HKCU\...\Run`.
- Quyền Admin: tạo tác vụ Task Scheduler `ScreenSnap_AutoStart` (*Run with highest privileges*) để khởi động không bị hỏi UAC. Cần quyền Admin khi muốn dùng phím tắt lúc đang mở cửa sổ chạy quyền Admin (Task Manager, game...).
- Cài đặt lưu tại `%APPDATA%\ScreenSnap\settings.ini`, ảnh mặc định lưu ở `Ảnh\ScreenSnap`.

## Build lại từ mã nguồn
```
powershell -ExecutionPolicy Bypass -File build.ps1
```
Mã nguồn: `src\ScreenSnap.cs` (C# WinForms, biên dịch bằng `csc.exe` có sẵn trong Windows).
