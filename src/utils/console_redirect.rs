use std::fs::OpenOptions;
use std::io::Write;
use std::sync::{Arc, Mutex};
use chrono::Local;

// 全局日志文件句柄
static LOG_FILE: std::sync::OnceLock<Arc<Mutex<Option<std::fs::File>>>> = std::sync::OnceLock::new();

/// 简单的控制台错误重定向器
pub struct ConsoleRedirector {
    _marker: (),
}

impl ConsoleRedirector {
    /// 初始化控制台错误重定向
    pub fn init() -> Result<Self, Box<dyn std::error::Error>> {
        // 只创建logs目录，不创建文件
        std::fs::create_dir_all("logs")?;
        
        // 初始化全局日志文件句柄为None，表示还没有创建文件
        let log_file = Arc::new(Mutex::new(None));
        LOG_FILE.set(log_file.clone()).map_err(|_| "Failed to initialize log file")?;
        
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
            
            // 确保日志文件存在并写入panic信息
            ensure_log_file_and_write(&log_file_clone, &panic_log);
            
            // 如果有控制台，也输出到控制台（可选）
            #[cfg(debug_assertions)]
            eprintln!("{}", panic_log.trim());
        }));
        
        // 只在debug模式下显示启动信息
        #[cfg(debug_assertions)]
        println!("控制台错误输出重定向已启用，错误信息将保存到logs目录");
        
        Ok(ConsoleRedirector {
            _marker: (),
        })
    }
}

/// 确保日志文件存在并写入内容
fn ensure_log_file_and_write(log_file: &Arc<Mutex<Option<std::fs::File>>>, content: &str) {
    let mut file_guard = match log_file.lock() {
        Ok(guard) => guard,
        Err(_) => return,
    };
    
    // 如果文件还没有创建，现在创建它
    if file_guard.is_none() {
        let now = Local::now();
        let log_file_path = format!("logs/console_errors_{}.log", now.format("%Y%m%d"));
        
        match OpenOptions::new()
            .create(true)
            .append(true)
            .open(&log_file_path)
        {
            Ok(file) => {
                *file_guard = Some(file);
                #[cfg(debug_assertions)]
                println!("日志文件已创建: {}", log_file_path);
            }
            Err(_) => return,
        }
    }
    
    // 写入内容
    if let Some(ref mut file) = *file_guard {
        let _ = file.write_all(content.as_bytes());
        let _ = file.flush();
    }
}

/// 记录错误信息到日志文件
pub fn log_error(message: &str) {
    if let Some(log_file) = LOG_FILE.get() {
        let now = Local::now();
        let timestamp = now.format("%Y-%m-%d %H:%M:%S%.3f");
        let log_entry = format!("[{}] ERROR: {}\n", timestamp, message);
        
        // 确保日志文件存在并写入错误信息
        ensure_log_file_and_write(log_file, &log_entry);
    }
    
    // 同时输出到stderr（保持原有行为）
    eprintln!("{}", message);
}

 