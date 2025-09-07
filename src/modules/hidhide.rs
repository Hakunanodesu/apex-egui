use std::process::{Command, Stdio};
use std::os::windows::process::CommandExt;
use std::io;
use std::path::Path;

const CREATE_NO_WINDOW: u32 = 0x0800_0000;
const HIDHIDE_CLI_PATH: &str = r"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe";

/// Run HidHideCLI.exe, hiding the console window. 
/// Returns a RunResult with success flag, stdout and stderr.
/// Returns an error if HidHide is not installed or if the command fails.
pub fn run_hidhidecli(args: &[&str]) -> io::Result<String> {
    // 检查 HidHide CLI 是否存在
    if !Path::new(HIDHIDE_CLI_PATH).exists() {
        return Err(io::Error::new(
            io::ErrorKind::NotFound,
            "HidHide CLI not found. Please install HidHide first."
        ));
    }

    let output = Command::new(HIDHIDE_CLI_PATH)
        .args(args)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .creation_flags(CREATE_NO_WINDOW)
        .output()?;  // 如果启动失败或等待子进程出错，这里会返回 Err

    let stdout = String::from_utf8_lossy(&output.stdout).into_owned();

    Ok(stdout)
}