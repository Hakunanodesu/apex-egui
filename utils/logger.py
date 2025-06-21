import logging
import sys
from datetime import datetime
from pathlib import Path


class Logger:
    """日志管理器类"""
    
    def __init__(self, name="TargetDetect", log_dir="logs"):
        self.name = name
        self.log_dir = Path(log_dir)
        self.log_dir.mkdir(exist_ok=True)
        
        # 创建日志文件名（包含时间戳）
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        self.log_file = self.log_dir / f"{name}_{timestamp}.log"
        
        # 配置日志格式
        self.formatter = logging.Formatter(
            '%(asctime)s [%(levelname)s] %(name)s: %(message)s',
            datefmt='%Y-%m-%d %H:%M:%S'
        )
        
        # 创建日志记录器
        self.logger = logging.getLogger(name)
        self.logger.setLevel(logging.DEBUG)
        
        # 清除现有的处理器
        self.logger.handlers.clear()
        
        # 添加文件处理器
        self.file_handler = logging.FileHandler(self.log_file, encoding='utf-8')
        self.file_handler.setLevel(logging.DEBUG)
        self.file_handler.setFormatter(self.formatter)
        self.logger.addHandler(self.file_handler)
        
        # 添加控制台处理器
        self.console_handler = logging.StreamHandler(sys.stdout)
        self.console_handler.setLevel(logging.INFO)
        self.console_handler.setFormatter(self.formatter)
        self.logger.addHandler(self.console_handler)
        
        # 记录启动信息
        self.info(f"日志系统初始化完成，日志文件: {self.log_file}")
    
    def debug(self, message):
        """调试级别日志"""
        self.logger.debug(message)
    
    def info(self, message):
        """信息级别日志"""
        self.logger.info(message)
    
    def warning(self, message):
        """警告级别日志"""
        self.logger.warning(message)
    
    def error(self, message):
        """错误级别日志"""
        self.logger.error(message)
    
    def critical(self, message):
        """严重错误级别日志"""
        self.logger.critical(message)
    
    def exception(self, message="发生异常"):
        """记录异常信息（包含完整的堆栈跟踪）"""
        self.logger.exception(message)
    
    def log_exception(self, e, context=""):
        """记录异常和上下文信息"""
        if context:
            self.error(f"{context}: {str(e)}")
        else:
            self.error(f"异常: {str(e)}")
        self.exception("详细堆栈信息:")
    
    def log_function_call(self, func_name, args=None, kwargs=None):
        """记录函数调用"""
        args_str = ""
        if args:
            args_str += f"args={args}"
        if kwargs:
            if args_str:
                args_str += ", "
            args_str += f"kwargs={kwargs}"
        
        self.debug(f"调用函数: {func_name}({args_str})")
    
    def log_function_return(self, func_name, result=None):
        """记录函数返回"""
        if result is not None:
            self.debug(f"函数 {func_name} 返回: {result}")
        else:
            self.debug(f"函数 {func_name} 执行完成")
    
    def log_performance(self, operation, duration_ms):
        """记录性能信息"""
        self.info(f"性能 - {operation}: {duration_ms:.2f}ms")
    
    def cleanup_old_logs(self, keep_days=7):
        """清理旧日志文件"""
        try:
            cutoff_time = datetime.now().timestamp() - (keep_days * 24 * 60 * 60)
            count = 0
            
            for log_file in self.log_dir.glob(f"{self.name}_*.log"):
                if log_file.stat().st_mtime < cutoff_time:
                    log_file.unlink()
                    count += 1
            
            if count > 0:
                self.info(f"清理了 {count} 个旧日志文件")
        except Exception as e:
            self.error(f"清理旧日志文件时出错: {e}")


# 全局日志实例
_logger_instance = None


def get_logger(name="TargetDetect"):
    """获取全局日志实例"""
    global _logger_instance
    if _logger_instance is None:
        _logger_instance = Logger(name)
    return _logger_instance


def log_function(func):
    """装饰器：自动记录函数调用和返回"""
    def wrapper(*args, **kwargs):
        logger = get_logger()
        func_name = func.__name__
        
        logger.log_function_call(func_name, args, kwargs)
        
        try:
            result = func(*args, **kwargs)
            logger.log_function_return(func_name, result)
            return result
        except Exception as e:
            logger.log_exception(e, f"函数 {func_name} 执行失败")
            raise
    
    return wrapper


def log_exception(func):
    """装饰器：自动记录异常"""
    def wrapper(*args, **kwargs):
        logger = get_logger()
        func_name = func.__name__
        
        try:
            return func(*args, **kwargs)
        except Exception as e:
            logger.log_exception(e, f"函数 {func_name} 发生异常")
            raise
    
    return wrapper


# 便捷函数
def debug(msg): get_logger().debug(msg)
def info(msg): get_logger().info(msg)
def warning(msg): get_logger().warning(msg)
def error(msg): get_logger().error(msg)
def critical(msg): get_logger().critical(msg)
def exception(msg="发生异常"): get_logger().exception(msg) 