//! 枪械识别线程：从右下角 ROI 做 Sobel + SSIM 匹配，返回最相似模板名（无后缀）
//! 模板图片在编译时通过 build.rs 嵌入二进制，无需运行时 gun_templates 目录。
//! 约束（非常重要）：模板文件被视为“最终特征图”，禁止再对模板做 Sobel/Canny/dilate/拉伸等二次处理。

include!("../build/gun_templates.rs");

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use anyhow::Result;
use fast_image_resize as fir;
use fast_image_resize::images::Image;
use fast_image_resize::{PixelType, ResizeOptions, Resizer};
use image::{GrayImage, ImageBuffer, Luma};
use imageproc::gradients::sobel_gradients;
use std::num::NonZeroU32;

use crate::shared_constants::weapon_rec::{
    EMPTY_HAND_SSIM_THRESHOLD, EMPTY_HAND_STR, TEMPLATE_H as WEAPON_TEMPLATE_H,
    TEMPLATE_W as WEAPON_TEMPLATE_W,
};
use crate::utils::console_redirect::log_error;

struct WeaponTemplate {
    /// 模板名（通常为文件名无后缀）
    name: String,
    /// 模板最终特征图（由外部准备，通常就是 live 预览导出的 Sobel 图）
    /// 注意：该图禁止在本模块内再次经过 Sobel/Canny/dilate/拉伸等处理。
    feature: GrayImage,
}

/// 从编译时嵌入的 TEMPLATE_FILES 解码出模板。
/// 模板在编译期已转为灰度原始字节，这里直接还原 GrayImage，不做额外处理。
fn load_embedded_templates() -> Vec<WeaponTemplate> {
    let mut out = Vec::new();
    for (name, w, h, bytes) in TEMPLATE_FILES.iter() {
        let luma = match GrayImage::from_raw(*w, *h, bytes.to_vec()) {
            Some(img) => img,
            None => {
                log_error(&format!(
                    "还原嵌入模板 {} 失败：尺寸 {}x{} 与字节长度 {} 不匹配",
                    name,
                    w,
                    h,
                    bytes.len()
                ));
                continue;
            }
        };

        out.push(WeaponTemplate {
            name: name.to_string(),
            feature: luma,
        });
    }
    out
}

/// 将 HWC RGB Vec<u8> 转为 image GrayImage
fn rgb_to_gray(rgb: &[u8], w: usize, h: usize) -> Option<GrayImage> {
    if rgb.len() < w * h * 3 {
        return None;
    }
    let mut buf = ImageBuffer::<Luma<u8>, Vec<u8>>::new(w as u32, h as u32);
    for y in 0..h {
        for x in 0..w {
            let i = (y * w + x) * 3;
            let r = rgb[i] as f32;
            let g = rgb[i + 1] as f32;
            let b = rgb[i + 2] as f32;
            let luma = (0.299 * r + 0.587 * g + 0.114 * b).round() as u8;
            buf.put_pixel(x as u32, y as u32, Luma([luma]));
        }
    }
    Some(buf)
}

/// 将任意尺寸的灰度图缩放到模板统一尺寸：
/// - 下采样使用 Box（近似 INTER_AREA）
/// - 上采样使用 Bilinear（近似 INTER_LINEAR）
fn resize_gray_to_target(src: &GrayImage) -> GrayImage {
    let (w, h) = (src.width(), src.height());
    if w == WEAPON_TEMPLATE_W && h == WEAPON_TEMPLATE_H {
        return src.clone();
    }
    let src_width = NonZeroU32::new(w).unwrap();
    let src_height = NonZeroU32::new(h).unwrap();

    let src_image = Image::from_vec_u8(
        src_width.get(),
        src_height.get(),
        src.as_raw().to_vec(),
        PixelType::U8,
    )
    .expect("valid gray image buffer");
    let mut dst_image = Image::new(WEAPON_TEMPLATE_W, WEAPON_TEMPLATE_H, PixelType::U8);

    let downsample = w > WEAPON_TEMPLATE_W || h > WEAPON_TEMPLATE_H;
    let alg = if downsample {
        fir::ResizeAlg::Convolution(fir::FilterType::Box)
    } else {
        fir::ResizeAlg::Convolution(fir::FilterType::Bilinear)
    };

    let mut resizer = Resizer::new();
    let options = ResizeOptions::new().resize_alg(alg);
    resizer
        .resize(&src_image, &mut dst_image, &options)
        .expect("resize ok");

    GrayImage::from_raw(WEAPON_TEMPLATE_W, WEAPON_TEMPLATE_H, dst_image.buffer().to_vec())
        .expect("size matches weapon template size")
}

/// 计算 Sobel 梯度幅值图并归一化到 0-255
fn sobel_magnitude(gray: &GrayImage) -> GrayImage {
    let (w, h) = (gray.width(), gray.height());
    if w == 0 || h == 0 {
        return GrayImage::new(w, h);
    }

    let grad = sobel_gradients(gray); // u16
    let mut max_v = 0u16;
    for p in grad.pixels() {
        max_v = max_v.max(p[0]);
    }
    if max_v == 0 {
        return GrayImage::new(w, h);
    }

    let mut out = GrayImage::new(w, h);
    for (x, y, p) in grad.enumerate_pixels() {
        let v = p[0] as f32 / max_v as f32;
        out.put_pixel(x, y, Luma([(v * 255.0).clamp(0.0, 255.0) as u8]));
    }
    out
}

/// 全局 SSIM，相似度范围 [0, 1]
fn ssim(a: &GrayImage, b: &GrayImage) -> f32 {
    let (wa, ha) = (a.width(), a.height());
    let (wb, hb) = (b.width(), b.height());
    if wa != wb || ha != hb || wa == 0 || ha == 0 {
        return 0.0;
    }

    let n = (wa as f32) * (ha as f32);
    if n <= 0.0 {
        return 0.0;
    }

    let mut sum_a = 0.0f32;
    let mut sum_b = 0.0f32;
    let mut sum_a2 = 0.0f32;
    let mut sum_b2 = 0.0f32;
    let mut sum_ab = 0.0f32;
    for y in 0..ha {
        for x in 0..wa {
            let va = a.get_pixel(x, y)[0] as f32;
            let vb = b.get_pixel(x, y)[0] as f32;
            sum_a += va;
            sum_b += vb;
            sum_a2 += va * va;
            sum_b2 += vb * vb;
            sum_ab += va * vb;
        }
    }

    let mean_a = sum_a / n;
    let mean_b = sum_b / n;
    let var_a = (sum_a2 / n) - mean_a * mean_a;
    let var_b = (sum_b2 / n) - mean_b * mean_b;
    let cov_ab = (sum_ab / n) - mean_a * mean_b;

    let k1 = 0.01f32;
    let k2 = 0.03f32;
    let l = 255.0f32;
    let c1 = (k1 * l) * (k1 * l);
    let c2 = (k2 * l) * (k2 * l);
    let num = (2.0 * mean_a * mean_b + c1) * (2.0 * cov_ab + c2);
    let den = (mean_a * mean_a + mean_b * mean_b + c1) * (var_a + var_b + c2);

    if den.abs() <= f32::EPSILON {
        0.0
    } else {
        (num / den).clamp(0.0, 1.0)
    }
}

pub struct WeaponRecThread {
    stop_flag: Arc<AtomicBool>,
    handle: Option<JoinHandle<()>>,
    result: Arc<Mutex<String>>,
    match_latency_ms: Arc<Mutex<f32>>,
    /// 最近一帧与最佳模板的相似度 [0, 1]
    best_similarity: Arc<Mutex<f32>>,
    /// 最近一帧的特征图（当前为 Sobel 梯度幅值），用于预览。
    canny_pixels: Arc<Mutex<Option<Vec<u8>>>>,
    error_flag: Arc<AtomicBool>,
}

impl WeaponRecThread {
    /// 启动枪械识别线程。模板在编译时已嵌入二进制，常驻内存。
    pub fn start(
        buffer2: Arc<Mutex<Vec<u8>>>,
        version2: Arc<AtomicU64>,
        crop_size: Arc<Mutex<(usize, usize)>>,
    ) -> Result<Self> {
        let templates = load_embedded_templates();
        if templates.is_empty() {
            log_error("未嵌入任何武器模板，枪械识别将始终返回空");
        }

        let stop_flag = Arc::new(AtomicBool::new(false));
        let result = Arc::new(Mutex::new(String::new()));
        let match_latency_ms = Arc::new(Mutex::new(0.0f32));
        let best_similarity = Arc::new(Mutex::new(0.0f32));
        let canny_pixels = Arc::new(Mutex::new(None));
        let error_flag = Arc::new(AtomicBool::new(false));

        let stop_clone = stop_flag.clone();
        let result_clone = result.clone();
        let match_latency_ms_clone = match_latency_ms.clone();
        let best_similarity_clone = best_similarity.clone();
        let canny_pixels_clone = canny_pixels.clone();
        let _error_flag_clone = error_flag.clone();

        let handle = thread::spawn(move || {
            let mut last_version = 0u64;
            while !stop_clone.load(Ordering::SeqCst) {
                let match_start = Instant::now();
                let current_version = version2.load(Ordering::Acquire);
                if current_version == last_version {
                    // 未检测到新的 ROI 图像版本时，短暂休眠以减少 CPU 占用
                    thread::sleep(Duration::from_millis(1));
                    continue;
                }
                let (roi_copy, crop_w, crop_h) = {
                    let buf = match buffer2.lock() {
                        Ok(g) => g,
                        Err(_) => {
                            thread::sleep(Duration::from_millis(5));
                            continue;
                        }
                    };
                    let (cw, ch) = match crop_size.lock() {
                        Ok(g) => *g,
                        Err(_) => {
                            thread::sleep(Duration::from_millis(5));
                            continue;
                        }
                    };
                    if buf.is_empty() || cw == 0 || ch == 0 {
                        last_version = current_version;
                        continue;
                    }
                    (buf.clone(), cw, ch)
                };
                last_version = current_version;

                // 1) 将 ROI RGB 转灰度
                let gray_raw = match rgb_to_gray(&roi_copy, crop_w, crop_h) {
                    Some(g) => g,
                    None => continue,
                };
                // 2) 缩放到统一尺寸（下采样 Box，上采样 Bilinear）
                let gray = resize_gray_to_target(&gray_raw);
                // 3) Sobel 梯度幅值图
                let live_sobel = sobel_magnitude(&gray);
                if let Ok(mut guard) = canny_pixels_clone.lock() {
                    *guard = Some(live_sobel.as_raw().to_vec());
                }

                let (best_name, best_sim) = templates
                    .iter()
                    .map(|t| (t.name.clone(), ssim(&live_sobel, &t.feature)))
                    .max_by(|a, b| a.1.partial_cmp(&b.1).unwrap_or(std::cmp::Ordering::Equal))
                    .unwrap_or((String::new(), 0.0));

                let result_str = if best_sim < EMPTY_HAND_SSIM_THRESHOLD {
                    EMPTY_HAND_STR.to_string()
                } else {
                    best_name
                };

                if let Ok(mut res) = result_clone.lock() {
                    *res = result_str;
                }
                if let Ok(mut guard) = best_similarity_clone.lock() {
                    *guard = best_sim;
                }
                let elapsed_ms = match_start.elapsed().as_secs_f32() * 1000.0;
                if let Ok(mut guard) = match_latency_ms_clone.lock() {
                    *guard = elapsed_ms;
                }
            }
        });

        Ok(Self {
            stop_flag,
            handle: Some(handle),
            result,
            match_latency_ms,
            best_similarity,
            canny_pixels,
            error_flag,
        })
    }

    pub fn result(&self) -> Arc<Mutex<String>> {
        self.result.clone()
    }

    /// 最近一帧的 live Sobel 特征图像素（模板统一尺寸灰度），用于推理预览窗口。
    pub fn canny_pixels(&self) -> Arc<Mutex<Option<Vec<u8>>>> {
        self.canny_pixels.clone()
    }

    /// 单次武器匹配耗时（ms）
    pub fn match_latency_ms(&self) -> Arc<Mutex<f32>> {
        self.match_latency_ms.clone()
    }

    /// 最近一帧与最佳模板的相似度 [0, 1]
    pub fn best_similarity(&self) -> Arc<Mutex<f32>> {
        self.best_similarity.clone()
    }

    pub fn error_flag(&self) -> Arc<AtomicBool> {
        self.error_flag.clone()
    }

    pub fn stop(mut self) {
        self.stop_flag.store(true, Ordering::SeqCst);
        if let Some(h) = self.handle.take() {
            let _ = h.join();
        }
    }
}
