import ctypes
from pathlib import Path
import os
import traceback
import sys
from pywinusb import hid


def handle_exception(e):
    sys.stdout.write(f"\n{traceback.format_exc()}\n")

def list_subdirs(path):
    # 列出 path 下的所有条目，并筛选出目录
    return [name for name in os.listdir(path)
            if os.path.isdir(os.path.join(path, name))]

def find_model_files():
    """
    在当前工作目录（及其子目录）中查找后缀为 .onnx 的文件，
    并返回它们相对于当前工作目录的路径列表。
    """
    cwd = Path.cwd()
    extensions = {'.onnx'}
    result = []

    for path in cwd.rglob('*'):
        if path.is_file() and path.suffix.lower() in extensions:
            # 将绝对路径转换为相对于 cwd 的相对路径
            rel_path = path.relative_to(cwd)
            result.append(str(rel_path).replace('\\', '/'))
    return result

def get_screenshot_region_dxcam(screenshot_size):
    user32 = ctypes.windll.user32
    screen_width = user32.GetSystemMetrics(0)
    screen_height = user32.GetSystemMetrics(1)

    region_left = (screen_width  - screenshot_size) // 2
    region_top  = (screen_height - screenshot_size) // 2

    region = (
        region_left, 
        region_top, 
        region_left + screenshot_size, 
        region_top + screenshot_size
    )
    return region

def get_scaling_factor():
    # 获取当前活动窗口的 DPI（仅支持 Windows 8.1 及以上）
    try:
        # 设置 DPI 感知
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # PROCESS_PER_MONITOR_DPI_AWARE = 2
    except:
        pass  # 某些旧系统不支持

    # 获取屏幕 DPI
    hdc = ctypes.windll.user32.GetDC(0)
    dpi = ctypes.windll.gdi32.GetDeviceCaps(hdc, 88)  # LOGPIXELSX
    ctypes.windll.user32.ReleaseDC(0, hdc)

    # 计算放大系数
    scaling = dpi / 96  # 96 是默认 DPI
    return scaling

def enum_hid_devices():
    """
    枚举所有 HID 设备，仅返回以下字段：
      - vendor_id    (hex 字符串)
      - product_id   (hex 字符串)
      - device_path  (原始路径)
      - product_name (HID 描述符名称)
    返回：
        List[Dict[str, str]]
    """
    devices = hid.find_all_hid_devices()
    info_set = set()
    for dev in devices:
        name = dev.product_name
        vid = hex(dev.vendor_id)
        pid = hex(dev.product_id)
        path = dev.device_path
        info_set.add((name, vid, pid, path))
    return info_set

def median_of_three(x, max, min): # 比min，max嵌套函数更快
    if x < min:
        return min
    elif x > max:
        return max
    else:
        return x
    

if __name__ == "__main__":
    print(enum_hid_devices())