use std::fs::OpenOptions;
use std::io::Write;
use std::sync::{Arc, Mutex};
use chrono::Local;

/// 简单的控制台错误重定向器
pub struct ConsoleRedirector {
    _log_file: Arc<Mutex<Option<std::fs::File>>>,
}

impl ConsoleRedirector {
    /// 初始化控制台错误重定向
    pub fn init() -> Result<Self, Box<dyn std::error::Error>> {
        let log_file = Arc::new(Mutex::new(None));
        
        // 设置panic hook来捕获panic信息
        let log_file_clone = log_file.clone();
        std::panic::set_hook(Box::new(move |panic_info| {
            let now = Local::now();
            let timestamp = now.format("%Y-%m-%d %H:%M:%S%.3f");
            
            let location = panic_info.location()
                .map(|l| format!("{}:{}:{}", l.file(), l.line(), l.column()))
                .unwrap_or_else(|| "unknown location".to_string());
            
            let message = if let Some(s) = panic_info.payload().downcast_ref::<&str>() {
                s.to_string()
            } else if let Some(s) = panic_info.payload().downcast_ref::<String>() {
                s.clone()
            } else {
                "Unknown panic message".to_string()
            };
            
            let panic_log = format!("[{}] PANIC at {}: {}\n", timestamp, location, message);
            
            // 只在发生panic时才创建日志文件和记录
            if let Ok(mut file_guard) = log_file_clone.lock() {
                if file_guard.is_none() {
                    // 创建logs目录
                    if let Ok(()) = std::fs::create_dir_all("logs") {
                        // 创建日志文件
                        let log_file_path = format!("logs/console_errors_{}.log", now.format("%Y%m%d"));
                        
                        if let Ok(file) = OpenOptions::new()
                            .create(true)
                            .append(true)
                            .open(&log_file_path) {
                            *file_guard = Some(file);
                        }
                    }
                }
                
                // 写入panic信息到文件
                if let Some(ref mut file) = *file_guard {
                    let _ = file.write_all(panic_log.as_bytes());
                    let _ = file.flush();
                }
            }
            
            // 如果有控制台，也输出到控制台（可选）
            #[cfg(debug_assertions)]
            eprintln!("{}", panic_log.trim());
        }));
        
        // 只在debug模式下显示启动信息
        #[cfg(debug_assertions)]
        println!("控制台错误输出重定向已启用，仅在发生错误时创建日志文件");
        
        Ok(ConsoleRedirector {
            _log_file: log_file,
        })
    }
}

 